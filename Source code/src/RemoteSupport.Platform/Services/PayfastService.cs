using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Taking the money through PayFast (2026-10-13, roadmap 4.8, <c>docs/NEXT-SESSION-PAYFAST.md</c> stage A): the account,
/// the checkout, the notices PayFast posts to <c>/api/payfast/notify</c>, and the subscription API. Every rule about
/// PayFast is here or in <see cref="PayfastWire"/>; what a payment MEANS for a company (switching it on, grace,
/// suspension) is stages B to D, which listen through <see cref="Listen"/>.
///
/// <para><b>A notice is believed only when all of these hold</b> (PayFast's own four checks, plus two of ours): it came
/// from PayFast's addresses; its signature is right with our passphrase; it names OUR merchant id; PayFast itself
/// answers "VALID" to it; it names a subscription we made (and, after the first, with the token PayFast gave it); and
/// the first payment is for what the checkout asked. Anything failing the first five is logged and dropped — never
/// stored. The sixth is stored as a payment NOT applied, because real money may have moved and a person must look.</para>
///
/// <para><b>Answering:</b> 200 to everything decided, so PayFast stops sending; 503 only when we could not decide —
/// PayFast's confirmation could not be reached, or the database failed — so PayFast sends it again.</para>
/// </summary>
public sealed class PayfastService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _cfg;
    private readonly ILogger<PayfastService> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>A notice's effect has been recorded: (subscription, payment, first payment of this subscription).</summary>
    private readonly List<Func<PayfastSubscription, PayfastPayment, bool, Task>> _listeners = new();

    /// <summary>
    /// Hears every notice that was ACTED ON: (subscription, payment, first payment of this subscription). Awaited before
    /// PayFast is answered, so what a payment means (a company switched on) has happened by the time anybody looks; a
    /// listener that fails is logged and does not undo the payment.
    /// </summary>
    public void Listen(Func<PayfastSubscription, PayfastPayment, bool, Task> listener) { lock (_listeners) _listeners.Add(listener); }

    public PayfastService(IDbContextFactory<PlatformDb> dbf, IDataProtectionProvider dp, IConfiguration cfg, ILogger<PayfastService> log)
    {
        _dbf = dbf; _cfg = cfg; _log = log;
        // Its own purpose string, as the mail password has: nothing holding another protector can read these.
        _protector = dp.CreateProtector("RemoteSupport.Payfast.v1");
    }

    // ---- the account -----------------------------------------------------------------------------------------

    public sealed record Account(string MerchantId, string MerchantKey, string Passphrase, bool Sandbox);

    /// <summary>The PayFast account, or null when none is set (or its secrets cannot be decrypted — data/keys moved).</summary>
    public async Task<Account?> AccountAsync()
    {
        var s = await SettingsAsync();
        if (!s.PayfastConfigured) return null;
        try { return new Account(s.PayfastMerchantId, _protector.Unprotect(s.PayfastMerchantKeyEnc), _protector.Unprotect(s.PayfastPassphraseEnc), s.PayfastSandbox); }
        catch (CryptographicException ex) { _log.LogWarning(ex, "PayFast's key could not be decrypted — data/keys has probably moved (handover section 10)"); return null; }
    }

    public async Task<PlatformSettings> SettingsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync() ?? new PlatformSettings();
    }

    /// <summary>
    /// Stores the account. Sandbox and live are only ever changed WITH a key and passphrase — a sandbox key cannot work
    /// against the live host — so switching the one without the others is refused. "" when done.
    /// </summary>
    public async Task<string> SetAccountAsync(string merchantId, string? merchantKey, string? passphrase, bool sandbox)
    {
        merchantId = (merchantId ?? "").Trim();
        if (!merchantId.All(char.IsDigit) || merchantId.Length is < 5 or > 12) return "The merchant id is the number on PayFast's Settings page.";
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.PlatformSettings.FirstOrDefaultAsync();
        if (s == null) { s = new PlatformSettings { Id = 1 }; db.PlatformSettings.Add(s); }
        bool newSecrets = !string.IsNullOrWhiteSpace(merchantKey) && !string.IsNullOrWhiteSpace(passphrase);
        if (!newSecrets && (!s.PayfastConfigured || s.PayfastSandbox != sandbox || s.PayfastMerchantId != merchantId))
            return "Give the merchant key and the passphrase too: they belong to one account, sandbox or live, and change with it.";
        if (!string.IsNullOrWhiteSpace(passphrase) && passphrase.Trim().Length < 8) return "That passphrase is too short to be the one on PayFast's Settings page.";
        s.PayfastMerchantId = merchantId;
        s.PayfastSandbox = sandbox;
        if (newSecrets)
        {
            s.PayfastMerchantKeyEnc = _protector.Protect(merchantKey!.Trim());
            s.PayfastPassphraseEnc = _protector.Protect(passphrase!.Trim());
        }
        s.PayfastUpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    // ---- where PayFast is ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>Testing:PayfastHost</c> sends everything — checkout, confirmation, API — to a harness's fake PayFast
    /// (tools/payfastcheck). Never set on a real server.
    /// </summary>
    private string? TestHost => _cfg["Testing:PayfastHost"] is { Length: > 0 } h ? h.TrimEnd('/') : null;

    private string Site(bool sandbox) => TestHost ?? (sandbox ? "https://sandbox.payfast.co.za" : "https://www.payfast.co.za");
    public string ProcessUrl(bool sandbox) => Site(sandbox) + "/eng/process";
    private string ValidateUrl(bool sandbox) => Site(sandbox) + "/eng/query/validate";
    private string ApiBase => TestHost ?? "https://api.payfast.co.za";

    /// <summary>Where a customer changes the card a subscription is charged to (stage C shows it on Billing).</summary>
    public string CardUpdateUrl(bool sandbox, string token, string returnTo) =>
        $"{Site(sandbox)}/eng/recurring/update/{Uri.EscapeDataString(token)}?return={Uri.EscapeDataString(returnTo)}";

    /// <summary>Our public address — what PayFast posts notices to and sends the customer back to.</summary>
    public string PublicUrl => (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
    public string NotifyUrl => PublicUrl + "/api/payfast/notify";

    // ---- the checkout ------------------------------------------------------------------------------------------

    /// <summary>What the browser posts to PayFast: the address and the fields, signature last.</summary>
    public sealed record Checkout(string Action, IReadOnlyList<KeyValuePair<string, string>> Fields, PayfastSubscription Subscription);

    public sealed record CheckoutRequest(
        int TenantId, string Purpose, string CreatedBy, string ItemName,
        long InitialCents, long AmountCents, string BillingDate, int Frequency, int Cycles,
        string ReturnUrl, string CancelUrl,
        string? FirstName = null, string? LastName = null, string? Email = null);

    /// <summary>
    /// Makes the subscription (awaiting its card) and the checkout that starts it. A company billed nothing, an amount
    /// under PayFast's R5.00 minimum, or a billing date in the past is refused here, before anybody reaches PayFast.
    /// </summary>
    public async Task<(Checkout? checkout, string error)> BeginAsync(CheckoutRequest q)
    {
        var acct = await AccountAsync();
        if (acct == null) return (null, "PayFast is not set up on this server (payfast set).");
        if (PublicUrl.Length == 0) return (null, "Platform:PublicUrl is not set, so PayFast would have nowhere to send its notice.");
        if (q.AmountCents < 500) return (null, "PayFast will not run a subscription for less than R5.00.");
        if (q.InitialCents < 0) return (null, "The first amount cannot be negative.");
        // PayFast's sandbox, 2026-10-13: "The frequency must be at least 3" — its documentation lists daily and weekly, its
        // engine refuses them. Monthly is the most often a subscription can run.
        if (q.Frequency is < 3 or > 6) return (null, "PayFast runs a subscription monthly at the most often (3 monthly, 4 quarterly, 5 six-monthly, 6 yearly).");
        if (q.Cycles < 0) return (null, "Cycles is 0 (until cancelled) or more.");
        if (!DateOnly.TryParseExact(q.BillingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bill)
            || bill < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)))
            return (null, "The billing date must be today or later, as yyyy-MM-dd.");
        await using var db = await _dbf.CreateDbContextAsync();
        if (!await db.Tenants.AnyAsync(t => t.Id == q.TenantId)) return (null, "No such company.");

        var sub = new PayfastSubscription
        {
            TenantId = q.TenantId, Reference = "rsp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant(),
            Purpose = q.Purpose, Sandbox = acct.Sandbox, CreatedBy = q.CreatedBy, ItemName = Trim(q.ItemName, 100),
            InitialCents = q.InitialCents, AmountCents = q.AmountCents, Frequency = q.Frequency, Cycles = q.Cycles,
            BillingDate = q.BillingDate, Status = PayfastStatus.AwaitingCard,
        };
        db.PayfastSubscriptions.Add(sub);
        await db.SaveChangesAsync();

        var fields = PayfastWire.CheckoutFields(new Dictionary<string, string?>
        {
            ["merchant_id"] = acct.MerchantId, ["merchant_key"] = acct.MerchantKey,
            // "{ref}" in either address becomes the reference — the return page needs it, and it is only made here.
            ["return_url"] = q.ReturnUrl.Replace("{ref}", sub.Reference), ["cancel_url"] = q.CancelUrl.Replace("{ref}", sub.Reference), ["notify_url"] = NotifyUrl,
            ["name_first"] = Trim(q.FirstName, 100), ["name_last"] = Trim(q.LastName, 100), ["email_address"] = Trim(q.Email, 100),
            ["m_payment_id"] = sub.Reference,
            ["amount"] = PayfastWire.Rand(q.InitialCents), ["item_name"] = sub.ItemName,
            ["custom_int1"] = q.TenantId.ToString(CultureInfo.InvariantCulture),
            ["subscription_type"] = "1", ["billing_date"] = q.BillingDate,
            ["recurring_amount"] = PayfastWire.Rand(q.AmountCents),
            ["frequency"] = q.Frequency.ToString(CultureInfo.InvariantCulture), ["cycles"] = q.Cycles.ToString(CultureInfo.InvariantCulture),
        });
        fields.Add(new("signature", PayfastWire.CheckoutSignature(fields, acct.Passphrase)));
        return (new Checkout(ProcessUrl(acct.Sandbox), fields, sub), "");
    }

    private static string Trim(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }

    // ---- the notices ---------------------------------------------------------------------------------------------

    public sealed record NoticeResult(int StatusCode, string Outcome);

    /// <summary>The last refusals, newest first, for the owner's PayFast page — the log has them too.</summary>
    public IReadOnlyList<(DateTime when, string from, string why)> RecentRefusals => _refusals.ToArray().Reverse().ToList();
    private readonly ConcurrentQueue<(DateTime, string, string)> _refusals = new();

    private NoticeResult Refuse(IPAddress? from, string why, string reference = "")
    {
        _log.LogWarning("PayFast notice refused ({Why}) from {From} {Reference}", why, from, reference);
        _refusals.Enqueue((DateTime.UtcNow, from?.ToString() ?? "?", why + (reference.Length > 0 ? " — " + reference : "")));
        while (_refusals.Count > 20) _refusals.TryDequeue(out _);
        return new NoticeResult(200, "refused: " + why);
    }

    // PayFast's host names resolve to addresses beyond its published ranges (the sandbox, at least): looked up, and
    // remembered ten minutes so a burst of notices does not mean a burst of lookups.
    private static (DateTime at, HashSet<IPAddress> ips) _resolved = (DateTime.MinValue, new());

    private async Task<bool> FromPayfastAsync(IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (PayfastWire.InRanges(ip)) return true;
        if (DateTime.UtcNow - _resolved.at > TimeSpan.FromMinutes(10))
        {
            var set = new HashSet<IPAddress>();
            foreach (var host in PayfastWire.PayfastHosts)
                try { foreach (var a in await Dns.GetHostAddressesAsync(host)) set.Add(a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a); }
                catch (Exception ex) { _log.LogInformation("Could not look up {Host}: {Why}", host, ex.Message); }
            _resolved = (DateTime.UtcNow, set);
        }
        return _resolved.ips.Contains(ip);
    }

    /// <summary>A notice, as PayFast posted it to <c>/api/payfast/notify</c>, from <paramref name="from"/>.</summary>
    public async Task<NoticeResult> HandleNoticeAsync(string body, IPAddress? from)
    {
        var posted = PayfastWire.ParseForm(body);
        string F(string k) => posted.FirstOrDefault(p => p.Key == k).Value ?? "";
        var reference = F("m_payment_id");

        // 1. From PayFast.
        if (!await FromPayfastAsync(from)) return Refuse(from, "not from a PayFast address", reference);

        // 2. Signed with our passphrase.
        var acct = await AccountAsync();
        if (acct == null) return Refuse(from, "PayFast is not set up on this server", reference);
        var paramString = PayfastWire.NoticeParamString(posted);
        if (!string.Equals(F("signature"), PayfastWire.NoticeSignature(paramString, acct.Passphrase), StringComparison.OrdinalIgnoreCase))
            return Refuse(from, "the signature is wrong", reference);

        // 3. For us.
        if (F("merchant_id") != acct.MerchantId) return Refuse(from, "another merchant's notice", reference);

        var status = F("payment_status").Trim().ToUpperInvariant();
        var pfId = F("pf_payment_id").Trim();
        var token = F("token").Trim();

        // 4. PayFast confirms it. Not reaching PayFast is not a "no": answered 503 so PayFast sends it again.
        string answer;
        try
        {
            using var res = await Http.PostAsync(ValidateUrl(acct.Sandbox), new StringContent(paramString, Encoding.UTF8, "application/x-www-form-urlencoded"));
            answer = (await res.Content.ReadAsStringAsync()).Trim();
            if (!res.IsSuccessStatusCode) { _log.LogWarning("PayFast's confirmation answered {Code}; the notice will come again", (int)res.StatusCode); return new NoticeResult(503, "could not confirm"); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("PayFast's confirmation could not be reached ({Why}); the notice will come again", ex.Message);
            return new NoticeResult(503, "could not confirm");
        }
        if (answer != "VALID")
        {
            // A CANCELLATION is never confirmed there (found on Clint's sandbox, 2026-10-13): it carries the id of the payment
            // that started the subscription with its status changed, and PayFast's confirmation, checking that payment,
            // answers INVALID. So a cancellation is confirmed by asking PayFast's API what the subscription IS — a stronger
            // answer than the confirmation's, since it comes from the subscription itself.
            if (status != "CANCELLED" || token.Length == 0) return Refuse(from, $"PayFast did not confirm it ({Short(answer)})", reference);
            var (state, problem) = await StateAsync(token);
            if (state == null)
            {
                _log.LogWarning("Could not ask PayFast's API about a cancellation ({Why}); the notice will come again", problem);
                return new NoticeResult(503, "could not confirm");
            }
            if (state != "CANCELLED") return Refuse(from, $"a cancellation, but PayFast's API says the subscription is {Short(state)}", reference);
        }

        // 5. A subscription we made — and, once it has a token, that token.
        if (pfId.Length == 0) return Refuse(from, "no pf_payment_id", reference);
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var sub = reference.Length == 0 ? null : await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Reference == reference);
            if (sub == null && token.Length > 0) sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Token == token);
            if (sub == null) return Refuse(from, "no subscription of ours has that reference", reference);
            if (sub.Token != null && token.Length > 0 && sub.Token != token) return Refuse(from, "the token is not this subscription's", reference);
            if (sub.Sandbox != acct.Sandbox) return Refuse(from, sub.Sandbox ? "a sandbox subscription's notice, and this server is live" : "a live subscription's notice, and this server is on the sandbox", reference);

            if (await db.PayfastPayments.AnyAsync(p => p.PfPaymentId == pfId && p.Status == status))
                return new NoticeResult(200, "already recorded");

            bool first = sub.Token == null && status == "COMPLETE";
            long gross = PayfastWire.Cents(F("amount_gross")) ?? -1;
            var pay = new PayfastPayment
            {
                TenantId = sub.TenantId, SubscriptionId = sub.Id, PfPaymentId = pfId, Status = status,
                AmountGrossCents = gross, AmountFeeCents = PayfastWire.Cents(F("amount_fee")) ?? 0, AmountNetCents = PayfastWire.Cents(F("amount_net")) ?? 0,
                ExpectedCents = first ? sub.InitialCents : sub.AmountCents,
                BillingDate = F("billing_date").Trim(), ItemName = Trim(F("item_name"), 100), Sandbox = acct.Sandbox,
            };

            // 6. What it does.
            switch (status)
            {
                case "COMPLETE" when first && gross != sub.InitialCents:
                    // The checkout's amount is inside our signature, so this is not a customer editing the form — it is
                    // something we do not understand. Kept, NOT acted on: the company is not switched on by it.
                    pay.Problem = $"the first payment was {PayfastWire.Rand(gross)}, not the {PayfastWire.Rand(sub.InitialCents)} the checkout asked";
                    break;
                case "COMPLETE":
                    if (!first && gross != sub.AmountCents)
                        // A renewal is PayFast charging what it was told; a difference means our record and theirs have
                        // drifted (a seat change mid-run). The money is real, so it counts — and it is said.
                        pay.Problem = $"renewal of {PayfastWire.Rand(gross)}, expected {PayfastWire.Rand(sub.AmountCents)} — counted";
                    pay.Applied = true;
                    if (token.Length > 0) sub.Token ??= token;
                    if (sub.Status != PayfastStatus.Cancelled) sub.Status = PayfastStatus.Active;
                    sub.ActivatedUtc ??= DateTime.UtcNow;
                    sub.LastPaymentUtc = DateTime.UtcNow;
                    if (pay.BillingDate.Length > 0) sub.NextRunDate = pay.BillingDate;
                    break;
                case "CANCELLED":
                    pay.Applied = true;
                    if (token.Length > 0) sub.Token ??= token;
                    sub.Status = PayfastStatus.Cancelled;
                    sub.CancelledUtc ??= DateTime.UtcNow;
                    break;
                default:
                    pay.Problem = $"PayFast sent the status '{Short(status)}', which this server does not act on";
                    break;
            }
            db.PayfastPayments.Add(pay);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                if (await StoredAsync(pfId, status)) return new NoticeResult(200, "already recorded");   // the same notice, twice at once
                throw;
            }
            _log.LogInformation("PayFast {Status} {Amount} for company {Tenant} ({Reference}){Problem}", status, PayfastWire.Rand(Math.Max(0, gross)),
                sub.TenantId, sub.Reference, pay.Problem.Length > 0 ? " — " + pay.Problem : "");
            if (pay.Applied)
                foreach (var listen in _listeners.ToArray())
                    try { await listen(sub, pay, first); }
                    catch (Exception ex) { _log.LogError(ex, "A listener failed on PayFast payment {Id}", pay.Id); }
            return new NoticeResult(200, pay.Applied ? "recorded" : "recorded, not applied: " + pay.Problem);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _log.LogError(ex, "Could not record a PayFast notice; it will come again");
            return new NoticeResult(503, "could not record");
        }
    }

    private async Task<bool> StoredAsync(string pfId, string status)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastPayments.AnyAsync(p => p.PfPaymentId == pfId && p.Status == status);
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // ---- the API ---------------------------------------------------------------------------------------------------

    public sealed record ApiAnswer(bool Ok, int Code, string Body, string Error);

    /// <summary>
    /// One call to PayFast's API: <c>merchant-id</c>, <c>version</c>, <c>timestamp</c> and <c>signature</c> headers, the
    /// body form-encoded, and on the sandbox <c>?testing=true</c> (left out of the signature).
    /// </summary>
    private async Task<ApiAnswer> ApiAsync(HttpMethod method, string path, Dictionary<string, string>? body = null)
    {
        var acct = await AccountAsync();
        if (acct == null) return new ApiAnswer(false, 0, "", "PayFast is not set up on this server.");
        body ??= new();
        // South Africa's offset written out: PayFast assumes GMT+2 when there is none.
        var stamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        var signed = new Dictionary<string, string>(body) { ["merchant-id"] = acct.MerchantId, ["version"] = "v1", ["timestamp"] = stamp };
        using var req = new HttpRequestMessage(method, ApiBase + path + (acct.Sandbox ? "?testing=true" : ""));
        req.Headers.Add("merchant-id", acct.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", stamp);
        req.Headers.Add("signature", PayfastWire.ApiSignature(signed, acct.Passphrase));
        if (body.Count > 0) req.Content = new FormUrlEncodedContent(body);
        try
        {
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            bool ok = res.IsSuccessStatusCode && !text.Contains("\"status\":\"failed\"", StringComparison.Ordinal);
            return new ApiAnswer(ok, (int)res.StatusCode, text, ok ? "" : $"PayFast answered {(int)res.StatusCode}: {Message(text)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ApiAnswer(false, 0, "", "PayFast's API could not be reached: " + ex.Message);
        }
    }

    /// <summary>PayFast's own words from an error answer, else the start of what it said.</summary>
    private static string Message(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m)) return m.ToString();
            if (doc.RootElement.TryGetProperty("status", out var st)) return st.ToString();
        }
        catch (JsonException) { }
        return Short(text.Trim());
    }

    /// <summary>Whether PayFast accepts our merchant id, passphrase and address — "API V1" from the sandbox.</summary>
    public Task<ApiAnswer> PingAsync() => ApiAsync(HttpMethod.Get, "/ping");

    public Task<ApiAnswer> FetchAsync(string token) => ApiAsync(HttpMethod.Get, $"/subscriptions/{Uri.EscapeDataString(token)}/fetch");

    /// <summary>A new amount from the next charge on; PayFast takes CENTS here, not rand.</summary>
    public Task<ApiAnswer> UpdateAmountAsync(string token, long cents) =>
        ApiAsync(HttpMethod.Patch, $"/subscriptions/{Uri.EscapeDataString(token)}/update",
            new() { ["amount"] = cents.ToString(CultureInfo.InvariantCulture) });

    public Task<ApiAnswer> CancelAsync(string token) => ApiAsync(HttpMethod.Put, $"/subscriptions/{Uri.EscapeDataString(token)}/cancel");

    /// <summary>
    /// What PayFast's API says a subscription is — its <c>status_text</c> upper-cased ("ACTIVE", "CANCELLED", "PAUSED", …).
    /// Null only when PayFast could not be asked (unreachable, or its own fault): then nothing is decided. A refusal from
    /// PayFast (no such token, say) is an answer, and says so.
    /// </summary>
    public async Task<(string? state, string problem)> StateAsync(string token)
    {
        var (state, _, problem) = await LookAsync(token);
        return (state, problem);
    }

    /// <summary>
    /// <see cref="StateAsync"/>, with the next charge date PayFast holds ("yyyy-MM-dd" from its <c>run_date</c>, or "") —
    /// stage D asks it before calling a payment late: a date that has moved on means PayFast was paid and only its
    /// notice went missing.
    /// </summary>
    public async Task<(string? state, string runDate, string problem)> LookAsync(string token)
    {
        var r = await FetchAsync(token);
        if (r.Code == 0 || r.Code >= 500) return (null, "", r.Error);
        if (!r.Ok) return ($"not known to PayFast ({r.Error})", "", r.Error);
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            if (doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("response", out var resp)
                && resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("status_text", out var st))
            {
                var run = resp.TryGetProperty("run_date", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() ?? "" : "";
                return ((st.GetString() ?? "").Trim().ToUpperInvariant(), run.Length >= 10 ? run[..10] : "", "");
            }
        }
        catch (JsonException) { }
        return ($"unreadable ({Short(r.Body)})", "", "PayFast's answer could not be read");
    }

    /// <summary>
    /// Asks PayFast what one of our subscriptions is, and puts ours right: Active or Cancelled as PayFast says. For a
    /// notice that never came or was refused — the owner's <i>Check with PayFast</i>. What PayFast said, in words.
    /// </summary>
    public async Task<(bool ok, string said)> CheckAsync(int subscriptionId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (sub == null) return (false, "No such subscription.");
        if (string.IsNullOrEmpty(sub.Token)) return (false, "PayFast has not given this one a token — its card never went through, so there is nothing at PayFast to ask about.");
        var acct = await AccountAsync();
        if (acct == null || acct.Sandbox != sub.Sandbox) return (false, sub.Sandbox ? "This is a sandbox subscription and the server is not on the sandbox account." : "This is a live subscription and the server is on the sandbox account.");
        var (state, run, problem) = await LookAsync(sub.Token);
        if (state == null) return (false, "PayFast could not be asked: " + problem);
        // PayFast's next charge date says whether a charge happened (2026-10-15: the sandbox's first R5.00 never showed —
        // a date moved on means PayFast charged and the notice went missing; a date gone by means PayFast never ran it).
        var next = "";
        if (state == "ACTIVE" && run.Length == 10)
            next = $" PayFast's next charge is {run}." + (string.CompareOrdinal(run, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0
                ? " That date has passed and PayFast has not charged it." : "");
        var was = sub.Status;
        if (state == "CANCELLED" && sub.Status != PayfastStatus.Cancelled) { sub.Status = PayfastStatus.Cancelled; sub.CancelledUtc ??= DateTime.UtcNow; }
        else if (state == "ACTIVE" && sub.Status == PayfastStatus.AwaitingCard) sub.Status = PayfastStatus.Active;
        await db.SaveChangesAsync();
        _log.LogInformation("PayFast says subscription {Reference} is {State}", sub.Reference, state);
        return (true, $"PayFast says {sub.Reference} is {state}." + (sub.Status != was ? $" Ours was {was}; it is {sub.Status} now." : " Ours agrees.") + next);
    }

    // ---- reading -------------------------------------------------------------------------------------------------

    public async Task<List<PayfastSubscription>> RecentSubscriptionsAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastSubscriptions.AsNoTracking().OrderByDescending(s => s.Id).Take(take).ToListAsync();
    }

    public async Task<PayfastSubscription?> ByReferenceAsync(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 40) return null;
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Reference == reference);
    }

    /// <summary>
    /// The subscription a company is paying by now, on the account this server holds (sandbox or live): the newest that
    /// is Active, else the newest of any kind — or null.
    /// </summary>
    public async Task<PayfastSubscription?> CurrentForAsync(int tenantId)
    {
        var acct = await AccountAsync();
        bool sandbox = acct?.Sandbox ?? true;
        await using var db = await _dbf.CreateDbContextAsync();
        var mine = db.PayfastSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && s.Sandbox == sandbox && s.Purpose != "test");
        return await mine.Where(s => s.Status == PayfastStatus.Active).OrderByDescending(s => s.Id).FirstOrDefaultAsync()
            ?? await mine.OrderByDescending(s => s.Id).FirstOrDefaultAsync();
    }

    public async Task<List<PayfastPayment>> RecentPaymentsAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastPayments.AsNoTracking().OrderByDescending(p => p.Id).Take(take).ToListAsync();
    }
}
