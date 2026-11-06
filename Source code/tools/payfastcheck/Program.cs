using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// payfastcheck — taking the money through PayFast, stage A (2026-10-13, docs/NEXT-SESSION-PAYFAST.md, roadmap 4.8).
//
// A real throwaway platform, and a FAKE PayFast in this harness standing in for PayFast's confirmation and API
// (Testing:PayfastHost). The signatures are worked out HERE from PayFast's documented rules, separately from the
// platform's own code, so the two must agree for anything to pass.
//
//   THE ACCOUNT     `payfast set` wants sandbox or live said, a real passphrase, and the key and passphrase changed WITH
//                   the mode; `payfast show` never prints a secret; the API ping is signed as PayFast checks it.
//   THE CHECKOUT    the owner's sandbox test checkout: PayFast's documented field order, our signature, the notify
//                   address, R0 now and R5.00 later — and NO antiforgery token (it would be outside the signature).
//   THE NOTICES     believed only from PayFast's addresses, rightly signed over the fields AS POSTED (blanks included),
//                   for our merchant, confirmed "VALID" by PayFast, for a subscription of ours with its own token.
//                   Refused ones are never stored; PayFast unreachable is a 503 (send again); the first payment for the
//                   wrong amount is kept but not acted on; a notice twice — even twice at once — is one payment.
//   THE SANDBOX     with tools\Save-PayfastSandbox.ps1 run, the same checkout is posted to PayFast's REAL sandbox and
//                   its API pinged with your sandbox account: accepted, and a tampered copy refused.
//
//   dotnet run -c Release --project tools/payfastcheck/payfastcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class PayfastCheck
{
    private static int _pass, _fail;
    private static string _proj = "", _dataDir = "", _base = "";
    private static readonly string AdminKey = Hex(24);
    private const string Merchant = "10000999", MerchantKey = "fakekey12345", Passphrase = "Harness-Salt-2026";
    private static readonly FakePayfast Fake = new();
    private const string PayfastIp = "197.97.145.150";   // inside PayFast's published 197.97.145.144/28

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Fake.Start();
        _dataDir = Path.Combine(Path.GetTempPath(), $"rs-payfastcheck-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(_dataDir);
        int port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        Console.WriteLine("payfastcheck — PayFast, stage A");
        Console.WriteLine($"platform on {port} ({HarnessDb.Name}), a fake PayFast on {Fake.Port}");

        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var buildOut = await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }

        // ---- the account, on the command line ------------------------------------------------------------------
        Head("the account: payfast set and show");
        await Cli(null, "create-tenant", "Pay Co", "pay-co", "5");
        var noMode = await Cli(Secrets(MerchantKey, Passphrase), "payfast", "set", "--merchant-id", Merchant);
        Check("sandbox or live must be said", noMode.Contains("--sandbox or --live"), First(noMode));
        var shortPhrase = await Cli(Secrets(MerchantKey, "short"), "payfast", "set", "--merchant-id", Merchant, "--sandbox");
        Check("a passphrase too short to be PayFast's is refused", shortPhrase.Contains("too short"), First(shortPhrase));
        var set = await Cli(Secrets(MerchantKey, Passphrase), "payfast", "set", "--merchant-id", Merchant, "--sandbox");
        Check("set: saved as the sandbox account", set.Contains("Saved: the SANDBOX account"), First(set));
        Check("and PayFast's API (the fake, checking the signature our way) accepts it", set.Contains("PayFast's API accepts it"), Last(set));
        var toLive = await Cli(null, "payfast", "set", "--merchant-id", Merchant, "--live");
        Check("switching to LIVE without that account's key and passphrase is refused", toLive.Contains("Give the merchant key and the passphrase too"), First(toLive));
        var show = await Cli(null, "payfast", "show");
        Check("show: still SANDBOX, the merchant, secrets only as 'stored'", show.Contains("SANDBOX") && show.Contains(Merchant) && show.Contains("stored"));
        Check("show never prints the key or the passphrase", !show.Contains(MerchantKey) && !show.Contains(Passphrase));
        Check("notices go to the public address's /api/payfast/notify", show.Contains(_base + "/api/payfast/notify"));
        if (!HarnessDb.Postgres && await File.ReadAllBytesAsync(Path.Combine(_dataDir, "platform.db")) is var db)
            Check("the database holds neither secret in plain text", db.Length > 0 && !Contains(db, MerchantKey) && !Contains(db, Passphrase));

        var platform = StartPlatform();
        try
        {
            if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var owner = NewBrowser();
            var step = await Post(owner, "/owner/login", await Html(owner, "/owner/login"), ("Key", AdminKey));
            _ownerStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var secret = _ownerSecret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            var landed = await PostFollow(owner, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(secret)));
            if (landed != "/owner") { Console.WriteLine("Could not sign in to /owner: " + landed); return Done(); }

            // ---- the page and the API --------------------------------------------------------------------------------
            Head("the owner's PayFast page");
            var page = await Html(owner, "/owner/payfast");
            Check("it says SANDBOX and names the merchant", page.Contains("data-mode=\"sandbox\"") && page.Contains(Merchant));
            Check("and never shows the key or passphrase", !page.Contains(MerchantKey) && !page.Contains(Passphrase));
            Fake.Pings.Clear();
            var pinged = await PortalPost(owner, "/owner/payfast", "Ping");
            Check("Test PayFast's API: accepted", pinged.Contains("API accepts this server (API V1)"), Snip(pinged, "alert"));
            var ping = Fake.Pings.LastOrDefault();
            Check("the ping carried merchant-id, version v1 and a timestamp with an offset",
                ping != null && ping.Headers.GetValueOrDefault("merchant-id") == Merchant && ping.Headers.GetValueOrDefault("version") == "v1"
                && Regex.IsMatch(ping.Headers.GetValueOrDefault("timestamp", ""), @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+02:00$"), ping?.Headers.GetValueOrDefault("timestamp"));
            Check("its signature is PayFast's API rule (sorted, with the passphrase, testing=true left out)", ping is { SignatureOk: true } && ping.Path.Contains("testing=true"), ping?.Path);

            // ---- the checkout ------------------------------------------------------------------------------------------
            Head("a sandbox test checkout");
            var tenantId = Regex.Match(page, "<option value=\"(\\d+)\">Pay Co</option>").Groups[1].Value;
            var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var under = await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "4.99"), ("billingDate", tomorrow), ("frequency", "3"), ("cycles", "2"));
            Check("under R5.00 a charge is refused before PayFast", under.Contains("less than R5.00"), Snip(under, "alert"));
            var past = await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "5.00"), ("billingDate", "2020-01-01"), ("frequency", "3"), ("cycles", "2"));
            Check("a billing date in the past is refused", past.Contains("today or later"), Snip(past, "alert"));
            var daily = await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "5.00"), ("billingDate", tomorrow), ("frequency", "1"), ("cycles", "2"));
            Check("daily is refused before PayFast (its sandbox refuses anything more often than monthly)", daily.Contains("monthly at the most often"), Snip(daily, "alert"));
            var go = await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "5.00"), ("billingDate", tomorrow), ("frequency", "3"), ("cycles", "2"));
            var form = CheckoutForm(go);
            Check("the page posts straight on to PayFast's /eng/process", form.action == Fake.Base + "/eng/process", form.action);
            var keys = form.fields.Select(f => f.Key).ToList();
            Check("no antiforgery token in it — it would fall outside PayFast's signature", !keys.Contains("__RequestVerificationToken"));
            Check("signature last, and every other field in PayFast's documented order",
                keys.LastOrDefault() == "signature" && keys.SkipLast(1).Select(k => Array.IndexOf(Order, k)).SequenceEqual(keys.SkipLast(1).Select(k => Array.IndexOf(Order, k)).OrderBy(i => i)) && keys.SkipLast(1).All(k => Array.IndexOf(Order, k) >= 0),
                string.Join(",", keys));
            string V(string k) => form.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            Check("our merchant and key; notices to us; back to the PayFast page",
                V("merchant_id") == Merchant && V("merchant_key") == MerchantKey && V("notify_url") == _base + "/api/payfast/notify" && V("return_url").StartsWith(_base + "/owner/payfast"));
            Check("a subscription: R0.00 now, R5.00 monthly from tomorrow, twice",
                V("subscription_type") == "1" && V("amount") == "0.00" && V("recurring_amount") == "5.00" && V("billing_date") == tomorrow && V("frequency") == "3" && V("cycles") == "2",
                string.Join(" ", form.fields.Where(f => f.Key is "amount" or "recurring_amount" or "billing_date" or "frequency" or "cycles").Select(f => f.Key + "=" + f.Value)));
            var reference = V("m_payment_id");
            Check("our reference goes as m_payment_id", Regex.IsMatch(reference, "^rsp-[0-9a-f]{20}$"), reference);
            Check("the signature is PayFast's checkout rule (documented order, trimmed, blanks out, passphrase last)",
                V("signature") == CheckoutSignature(form.fields.SkipLast(1), Passphrase), V("signature"));
            page = await Html(owner, "/owner/payfast");
            Check("the subscription is listed, awaiting its card", Sub(page, reference).Contains("data-status=\"AwaitingCard\""));

            // ---- the notices ---------------------------------------------------------------------------------------------
            Head("notices that must change nothing");
            var first = Notice(reference, "PF-1", "COMPLETE", "0.00", "tok-aaa", tomorrow);
            var (code, said) = await Notify(first, from: null);
            Check("from the server itself (no PayFast address): refused", code == 200 && said.Contains("not from a PayFast address"), said);
            (code, said) = await Notify(first, from: "8.8.8.8");
            Check("from somebody else's address: refused", said.Contains("not from a PayFast address"), said);
            (code, said) = await Notify(Resign(first, f => f, "not-the-passphrase"), from: PayfastIp);
            Check("signed with the wrong passphrase: refused", said.Contains("signature is wrong"), said);
            var shuffled = first.Where(f => f.Key != "signature").Reverse().Append(first.Last()).ToList();
            (code, said) = await Notify(shuffled, from: PayfastIp);
            Check("the same fields posted in another order: refused — the signature is over the order they came in", said.Contains("signature is wrong"), said);
            (code, said) = await Notify(Resign(first, f => Set(f, "merchant_id", "10000100"), Passphrase), from: PayfastIp);
            Check("another merchant's notice: refused", said.Contains("another merchant"), said);
            (code, said) = await Notify(Notice(reference, "INVALID-1", "COMPLETE", "0.00", "tok-aaa", tomorrow), from: PayfastIp);
            Check("PayFast does not confirm it (INVALID): refused", said.Contains("did not confirm"), said);
            (code, said) = await Notify(Notice("rsp-00000000000000000000", "PF-9", "COMPLETE", "0.00", "tok-zzz", tomorrow), from: PayfastIp);
            Check("a reference that is not ours: refused", said.Contains("no subscription of ours"), said);
            Fake.ValidateDown = true;
            (code, said) = await Notify(first, from: PayfastIp);
            Fake.ValidateDown = false;
            Check("PayFast's confirmation unreachable: 503, so PayFast sends it again", code == 503, $"{code} {said}");
            page = await Html(owner, "/owner/payfast");
            Check("after all of that: still awaiting its card, no payment stored", Sub(page, reference).Contains("AwaitingCard") && !page.Contains("id=\"payfastPayments\""));
            Check("and each refusal is listed for the owner", Regex.Matches(page, "<tr><td style=\"white-space:nowrap\">\\d{4}").Count >= 7);
            var validated = Fake.Validations.LastOrDefault() ?? "";
            Check("what went to PayFast's confirmation is the fields only — no signature, never the passphrase",
                validated.StartsWith("m_payment_id=") && !validated.Contains("signature=") && !validated.Contains("passphrase") && !validated.Contains(Passphrase), validated.Length > 80 ? validated[..80] + "…" : validated);

            Head("the first payment");
            (code, said) = await Notify(Notice(reference, "PF-0", "COMPLETE", "5.00", "tok-aaa", tomorrow), from: PayfastIp);
            Check("for the wrong amount (R5.00, the checkout asked R0.00): kept, NOT acted on", said.StartsWith("recorded, not applied"), said);
            page = await Html(owner, "/owner/payfast");
            Check("so the subscription is still awaiting its card", Sub(page, reference).Contains("AwaitingCard"));
            Check("and the payment is there, marked not acted on, saying why", Pay(page, "PF-0").Contains("data-applied=\"no\"") && Pay(page, "PF-0").Contains("not the 0.00 the checkout asked"));
            (code, said) = await Notify(first, from: PayfastIp);
            Check("the right one (blank fields signed as posted): recorded", code == 200 && said == "recorded", said);
            page = await Html(owner, "/owner/payfast");
            Check("the subscription is Active with PayFast's token", Sub(page, reference).Contains("data-status=\"Active\"") && Sub(page, reference).Contains("data-token=\"tok-aaa\""));
            (code, said) = await Notify(first, from: PayfastIp);
            Check("the same notice again: already recorded", said == "already recorded", said);

            Head("renewals and the end");
            var renewal = Notice(reference, "PF-2", "COMPLETE", "5.00", "tok-aaa", DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd"));
            var both = await Task.WhenAll(Notify(renewal, from: PayfastIp), Notify(renewal, from: PayfastIp));
            Check("a renewal sent twice AT ONCE: one recorded, one already recorded",
                both.Count(b => b.said == "recorded") == 1 && both.Count(b => b.said == "already recorded") == 1, string.Join(" / ", both.Select(b => $"{b.code} {b.said}")));
            page = await Html(owner, "/owner/payfast");
            Check("one payment row for it", Regex.Matches(page, "data-pf=\"PF-2\"").Count == 1);
            Check("the next charge date is the one PayFast gave", Sub(page, reference).Contains(DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd")));
            (code, said) = await Notify(Notice(reference, "PF-3", "COMPLETE", "5.00", "tok-bbb", tomorrow), from: PayfastIp);
            Check("a renewal naming another token: refused", said.Contains("token is not this subscription's"), said);
            (code, said) = await Notify(Notice(reference, "PF-4", "COMPLETE", "7.00", "tok-aaa", tomorrow), from: PayfastIp);
            Check("a renewal for a different amount: counted (the money is real) and said", said.StartsWith("recorded"), said);
            page = await Html(owner, "/owner/payfast");
            Check("…with the difference written on it", Pay(page, "PF-4").Contains("data-applied=\"yes\"") && Pay(page, "PF-4").Contains("expected 5.00"));
            // PayFast's confirmation answers INVALID to every cancellation (its real sandbox, 2026-10-13): the API decides.
            var cancel = Notice(reference, "PF-2", "CANCELLED", "0.00", "tok-aaa", "");
            (code, said) = await Notify(cancel, from: PayfastIp);
            Check("a cancellation PayFast's API knows nothing of: refused", said.Contains("PayFast's API says the subscription is not known to PayFast"), said);
            Fake.States["tok-aaa"] = "ACTIVE";
            (code, said) = await Notify(cancel, from: PayfastIp);
            Check("a cancellation while PayFast's API says ACTIVE: refused — a forged cancellation cannot end a customer's service", said.Contains("says the subscription is ACTIVE"), said);
            page = await Html(owner, "/owner/payfast");
            Check("so it is still Active", Sub(page, reference).Contains("data-status=\"Active\""));
            Fake.States["tok-aaa"] = "CANCELLED";
            (code, said) = await Notify(cancel, from: PayfastIp);
            Check("CANCELLED, which the confirmation calls INVALID but PayFast's API confirms: recorded", said == "recorded", said);
            page = await Html(owner, "/owner/payfast");
            Check("the subscription is Cancelled", Sub(page, reference).Contains("data-status=\"Cancelled\""));
            var checkedSame = await PortalPost(owner, "/owner/payfast", "Check", ("id", SubId(page, reference)));
            Check("Check with PayFast on it: PayFast says CANCELLED, ours agrees", checkedSame.Contains("is CANCELLED. Ours agrees."), Snip(checkedSame, "alert"));
            (code, said) = await Notify(Notice(reference, "PF-5", "COMPLETE", "5.00", "tok-aaa", tomorrow), from: PayfastIp);
            page = await Html(owner, "/owner/payfast");
            Check("a payment after cancelling is recorded but does not bring it back", Sub(page, reference).Contains("data-status=\"Cancelled\""));

            Head("Check with PayFast: a cancellation that never arrived");
            var go2 = CheckoutForm(await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "5.00"), ("billingDate", tomorrow), ("frequency", "3"), ("cycles", "2")));
            var ref2 = go2.fields.FirstOrDefault(f => f.Key == "m_payment_id").Value ?? "";
            page = await Html(owner, "/owner/payfast");
            Check("a subscription with no card yet has no Check button — nothing at PayFast to ask", !Sub(page, ref2).Contains("Check with PayFast"));
            (code, said) = await Notify(Notice(ref2, "PF-20", "COMPLETE", "0.00", "tok-ccc", tomorrow), from: PayfastIp);
            Fake.States["tok-ccc"] = "ACTIVE";
            var agreed = await PortalPost(owner, "/owner/payfast", "Check", ("id", SubId(await Html(owner, "/owner/payfast"), ref2)));
            Check("Check with PayFast on an active one: ours agrees, and PayFast's next charge date is said", agreed.Contains("is ACTIVE. Ours agrees.") && agreed.Contains("next charge is 2026-11-14.") && !agreed.Contains("has passed"), Snip(agreed, "alert"));
            Fake.RunDates["tok-ccc"] = "2026-10-01";
            var stale = await PortalPost(owner, "/owner/payfast", "Check", ("id", SubId(await Html(owner, "/owner/payfast"), ref2)));
            Check("a next charge date gone by is said: PayFast has not charged it", stale.Contains("next charge is 2026-10-01. That date has passed and PayFast has not charged it."), Snip(stale, "alert"));
            Fake.States["tok-ccc"] = "CANCELLED";   // cancelled at PayFast, and its notice lost
            page = await Html(owner, "/owner/payfast");
            Check("Active on ours, with the button now", Sub(page, ref2).Contains("data-status=\"Active\"") && Sub(page, ref2).Contains("Check with PayFast"));
            var put = await PortalPost(owner, "/owner/payfast", "Check", ("id", SubId(page, ref2)));
            Check("Check with PayFast: PayFast says CANCELLED, and ours is put right", put.Contains("is CANCELLED. Ours was Active; it is Cancelled now."), Snip(put, "alert"));
            Check("the page shows it Cancelled", Sub(put, ref2).Contains("data-status=\"Cancelled\""));
        }
        finally { try { platform.Kill(true); } catch { } }

        // ---- PayFast's real sandbox -------------------------------------------------------------------------------------
        Head("PayFast's REAL sandbox");
        var sandbox = SandboxAccount(root);
        if (sandbox == null)
            Console.WriteLine("  SKIP  no tools\\payfast-sandbox.xml — run tools\\Save-PayfastSandbox.ps1 to test against the real sandbox");
        else
            await RealSandbox(sandbox.Value);

        Fake.Dispose();
        return Done();
    }

    // ---- the real sandbox ---------------------------------------------------------------------------------------------

    private static async Task RealSandbox((string id, string key, string phrase) acct)
    {
        _realSandbox = true;
        var wrong = await Cli(Secrets(acct.key, acct.phrase + "x"), "payfast", "set", "--merchant-id", acct.id, "--sandbox");
        Check("a wrong passphrase: PayFast's real API refuses it", wrong.Contains("did not accept"), Last(wrong));
        var right = await Cli(Secrets(acct.key, acct.phrase), "payfast", "set", "--merchant-id", acct.id, "--sandbox");
        Check("your sandbox account: PayFast's real API accepts it", right.Contains("PayFast's API accepts it"), Last(right));
        int port = FreePort(); var keep = _base; _base = $"http://127.0.0.1:{port}";
        var platform = StartPlatform();
        try
        {
            if (!await WaitForPort(port, 90000)) { Check("the platform started", false); return; }
            // A code is good once (the owner's last step is stored): wait for the next one if the first sign-in was this window.
            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 <= _ownerStep + 1) await Task.Delay(1000);
            var owner = NewBrowser();
            var step = await Post(owner, "/owner/login", await Html(owner, "/owner/login"), ("Key", AdminKey));
            var landed = await PostFollow(owner, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(_ownerSecret)));
            if (landed != "/owner") { Check("signed in to /owner on the second platform", false, landed + " — step: " + Text(step) + " secret=" + _ownerSecret.Length); return; }
            var page = await Html(owner, "/owner/payfast");
            var tenantId = Regex.Match(page, "<option value=\"(\\d+)\">Pay Co</option>").Groups[1].Value;
            var go = await PortalPost(owner, "/owner/payfast", "Test", ("tenantId", tenantId), ("initial", "0.00"), ("amount", "5.00"),
                ("billingDate", DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd")), ("frequency", "3"), ("cycles", "2"));
            var form = CheckoutForm(go);
            Check("the checkout goes to sandbox.payfast.co.za", form.action == "https://sandbox.payfast.co.za/eng/process", form.action);
            // PayFast's own "Test your integration" would say the same; this is the real engine answering.
            var real = await PostToPayfast(form.action, form.fields);
            Check("PayFast's sandbox ACCEPTS our checkout and signature: on to its payment page", real.StartsWith("[302 https://sandbox.payfast.co.za/eng/process/payment/"), real.Length <= 220 ? real : real[..220]);
            var tampered = form.fields.Select(f => f.Key == "recurring_amount" ? new KeyValuePair<string, string>(f.Key, "6.00") : f).ToList();
            var refused = await PostToPayfast(form.action, tampered);
            Check("and refuses the same checkout with its amount changed (the signature covers it)", refused.Contains("Generated signature does not match"), Snip(refused, "invalid"));
        }
        finally { try { platform.Kill(true); } catch { } _base = keep; _realSandbox = false; }
    }

    private static bool _realSandbox;
    // The admin key's authenticator, set up at the first sign-in; the second platform (same data) only asks its code.
    private static string _ownerSecret = "";
    private static long _ownerStep;

    private static async Task<string> PostToPayfast(string action, IEnumerable<KeyValuePair<string, string>> fields)
    {
        using var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var r = await c.PostAsync(action, new FormUrlEncodedContent(fields));
        var text = await r.Content.ReadAsStringAsync();
        text = Regex.Replace(Regex.Replace(Regex.Replace(text, "(?s)<script.*?</script>|(?s)<style.*?</style>", " "), "<[^>]+>", " "), "\\s+", " ");
        return $"[{(int)r.StatusCode} {r.Headers.Location}] {text}";
    }

    private static (string id, string key, string phrase)? SandboxAccount(string root)
    {
        var file = Path.Combine(root, "tools", "payfast-sandbox.xml");
        if (!File.Exists(file)) return null;
        var ps = "$x = Import-Clixml '" + file.Replace("'", "''") + "'; function P($s) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }; $x.MerchantId; P $x.MerchantKey; P $x.Passphrase";
        var psi = new ProcessStartInfo("powershell", "-NoProfile -ExecutionPolicy Bypass -Command -") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        var p = Process.Start(psi)!;
        p.StandardInput.WriteLine(ps); p.StandardInput.Close();
        var lines = p.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        p.WaitForExit();
        return lines.Count >= 3 ? (lines[^3], lines[^2], lines[^1]) : null;
    }

    // ---- PayFast's rules, written here separately from the platform's ------------------------------------------------

    /// <summary>The checkout fields' documented order (developers.payfast.co.za/docs, 2026-10-13).</summary>
    private static readonly string[] Order =
    {
        "merchant_id", "merchant_key", "return_url", "cancel_url", "notify_url", "fica_idnumber", "name_first", "name_last", "email_address", "cell_number",
        "m_payment_id", "amount", "item_name", "item_description", "custom_int1", "custom_int2", "custom_int3", "custom_int4", "custom_int5",
        "custom_str1", "custom_str2", "custom_str3", "custom_str4", "custom_str5", "email_confirmation", "confirmation_address", "payment_method",
        "subscription_type", "billing_date", "recurring_amount", "frequency", "cycles", "subscription_notify_email", "subscription_notify_webhook", "subscription_notify_buyer",
    };

    /// <summary>PHP's urlencode, as PHP's manual defines it: [A-Za-z0-9-_.] kept, space '+', the rest %XX upper case.</summary>
    private static string PhpUrlencode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string CheckoutSignature(IEnumerable<KeyValuePair<string, string>> fields, string phrase) =>
        Md5(string.Join("&", fields.Where(f => f.Value.Trim().Length > 0).Select(f => f.Key + "=" + PhpUrlencode(f.Value.Trim()))) + "&passphrase=" + PhpUrlencode(phrase));

    private static string ApiSignature(Dictionary<string, string> values, string phrase)
    {
        var all = new SortedDictionary<string, string>(values.Where(v => v.Value.Length > 0).ToDictionary(v => v.Key, v => v.Value), StringComparer.Ordinal) { ["passphrase"] = phrase };
        return Md5(string.Join("&", all.Select(v => v.Key + "=" + PhpUrlencode(v.Value))));
    }

    /// <summary>A notice as PayFast's documentation shows one, fields in its order, blanks and all, signed.</summary>
    private static List<KeyValuePair<string, string>> Notice(string reference, string pfId, string status, string gross, string token, string billingDate)
    {
        var fee = gross == "0.00" ? "0.00" : "-0.58";
        var net = (decimal.Parse(gross, CultureInfo.InvariantCulture) + decimal.Parse(fee, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture);
        var f = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", reference), new("pf_payment_id", pfId), new("payment_status", status),
            new("item_name", "The Remote Support Program (sandbox test)"), new("item_description", ""),
            new("amount_gross", gross), new("amount_fee", fee), new("amount_net", net),
            new("custom_str1", ""), new("custom_str2", ""), new("custom_str3", ""), new("custom_str4", ""), new("custom_str5", ""),
            new("custom_int1", "1"), new("custom_int2", ""), new("custom_int3", ""), new("custom_int4", ""), new("custom_int5", ""),
            new("name_first", "Test"), new("name_last", "User 01"), new("email_address", "sbtu01@payfast.io"),
            new("merchant_id", Merchant), new("token", token), new("billing_date", billingDate),
        };
        return Resign(f, x => x, Passphrase);
    }

    private static List<KeyValuePair<string, string>> Resign(List<KeyValuePair<string, string>> f, Func<List<KeyValuePair<string, string>>, List<KeyValuePair<string, string>>> change, string phrase)
    {
        var body = change(f.Where(x => x.Key != "signature").ToList());
        var param = string.Join("&", body.Select(x => x.Key + "=" + PhpUrlencode(x.Value)));
        body.Add(new("signature", Md5(param + "&passphrase=" + PhpUrlencode(phrase))));
        return body;
    }

    private static List<KeyValuePair<string, string>> Set(List<KeyValuePair<string, string>> f, string key, string value) =>
        f.Select(x => x.Key == key ? new KeyValuePair<string, string>(key, value) : x).ToList();

    private static async Task<(int code, string said)> Notify(List<KeyValuePair<string, string>> fields, string? from)
    {
        using var c = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var body = string.Join("&", fields.Select(x => x.Key + "=" + PhpUrlencode(x.Value)));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/payfast/notify") { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        // The platform trusts X-Forwarded-For from loopback, as it does from Caddy — this is how PayFast's address reaches it.
        if (from != null) req.Headers.Add("X-Forwarded-For", from);
        using var r = await c.SendAsync(req);
        return ((int)r.StatusCode, (await r.Content.ReadAsStringAsync()).Trim());
    }

    // ---- reading the page -----------------------------------------------------------------------------------------------

    private static (string action, List<KeyValuePair<string, string>> fields) CheckoutForm(string html)
    {
        var i = html.IndexOf("id=\"toPayfast\"", StringComparison.Ordinal);
        if (i < 0) { Console.WriteLine("        no checkout form: " + Snip(html, "alert")); return ("", new()); }
        var formEnd = html.IndexOf("</form>", i, StringComparison.Ordinal);
        var formTag = html[html.LastIndexOf("<form", i, StringComparison.Ordinal)..html.IndexOf('>', i)];
        var action = WebUtility.HtmlDecode(Regex.Match(formTag, "action=\"([^\"]*)\"").Groups[1].Value);
        var fields = Regex.Matches(html[i..formEnd], "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();
        // Anything else posted with the form (a token the framework slipped in) counts too.
        foreach (Match m in Regex.Matches(html[i..formEnd], "<input name=\"([^\"]+)\" type=\"hidden\" value=\"([^\"]*)\""))
            fields.Add(new(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value)));
        return (action, fields);
    }

    private static string Sub(string html, string reference)
    {
        var i = html.IndexOf($"<tr data-ref=\"{reference}\"", StringComparison.Ordinal);
        return i < 0 ? "" : WebUtility.HtmlDecode(html[i..html.IndexOf("</tr>", i, StringComparison.Ordinal)]);
    }

    private static string SubId(string html, string reference) =>
        Regex.Match(Sub(html, reference), "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;

    private static string Pay(string html, string pfId)
    {
        var i = html.IndexOf($"data-pf=\"{pfId}\"", StringComparison.Ordinal);
        return i < 0 ? "" : WebUtility.HtmlDecode(html[i..html.IndexOf("</tr>", i, StringComparison.Ordinal)]);
    }

    // ---- a fake PayFast: the confirmation and the API ------------------------------------------------------------------

    private sealed record Ping(string Path, Dictionary<string, string> Headers, bool SignatureOk);

    private sealed class FakePayfast : IDisposable
    {
        private TcpListener? _l;
        public int Port;
        public string Base => $"http://127.0.0.1:{Port}";
        public volatile bool ValidateDown;
        public readonly ConcurrentQueue<string> ValidationsQ = new();
        public List<string> Validations => ValidationsQ.ToList();
        public readonly List<Ping> Pings = new();
        /// <summary>What the fake API says each subscription is, by token.</summary>
        public readonly ConcurrentDictionary<string, string> States = new();
        public readonly ConcurrentDictionary<string, string> RunDates = new();   // token → PayFast's next charge date (default 2026-11-14)

        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, 0); _l.Start(); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Serve(c); } });
        }

        private async Task Serve(TcpClient c)
        {
            using var _ = c;
            var s = c.GetStream();
            var head = new StringBuilder();
            var buf = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(buf) == 0) return; head.Append((char)buf[0]); }
            var lines = head.ToString().Split("\r\n");
            var parts = lines[0].Split(' ');
            var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            var body = "";
            if (headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) && n > 0)
            {
                var b = new byte[n]; int got = 0;
                while (got < n) { var r = await s.ReadAsync(b.AsMemory(got)); if (r == 0) break; got += r; }
                body = Encoding.UTF8.GetString(b, 0, got);
            }
            int code = 200; string answer;
            if (parts[1].StartsWith("/eng/query/validate"))
            {
                ValidationsQ.Enqueue(body);
                if (ValidateDown) { code = 500; answer = "down"; }
                // As PayFast's real sandbox did on 2026-10-13: a cancellation is never confirmed here.
                else answer = body.Contains("pf_payment_id=INVALID-") || body.Contains("payment_status=CANCELLED") ? "INVALID" : "VALID";
            }
            else if (parts[1].StartsWith("/subscriptions/") && parts[1].Contains("/fetch"))
            {
                var token = parts[1].Split('/')[2];
                var values = new Dictionary<string, string>
                {
                    ["merchant-id"] = headers.GetValueOrDefault("merchant-id", ""), ["version"] = headers.GetValueOrDefault("version", ""),
                    ["timestamp"] = headers.GetValueOrDefault("timestamp", ""),
                };
                if (headers.GetValueOrDefault("signature") != ApiSignature(values, Passphrase))
                { code = 401; answer = "{\"code\":401,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"Merchant authorisation failed\"}}"; }
                else if (!States.TryGetValue(token, out var state))
                { code = 404; answer = "{\"code\":404,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"Subscription not found\"}}"; }
                else answer = "{\"code\":200,\"status\":\"success\",\"data\":{\"response\":{\"amount\":500,\"cycles\":2,\"cycles_complete\":0,\"frequency\":3,"
                            + "\"run_date\":\"" + RunDates.GetValueOrDefault(token, "2026-11-14") + "T00:00:00+02:00\",\"status\":" + (state == "ACTIVE" ? 1 : 2) + ",\"status_reason\":\"\",\"status_text\":\"" + state + "\",\"token\":\"" + token + "\"}}}";
            }
            else if (parts[1].StartsWith("/ping"))
            {
                var values = new Dictionary<string, string>
                {
                    ["merchant-id"] = headers.GetValueOrDefault("merchant-id", ""), ["version"] = headers.GetValueOrDefault("version", ""),
                    ["timestamp"] = headers.GetValueOrDefault("timestamp", ""),
                };
                bool ok = headers.GetValueOrDefault("signature") == ApiSignature(values, Passphrase);
                lock (Pings) Pings.Add(new Ping(parts[1], headers, ok));
                if (ok) answer = "\"API V1\"";
                else { code = 401; answer = "{\"code\":401,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"Merchant authorisation failed\"}}"; }
            }
            else { code = 404; answer = "not here"; }
            var bytes = Encoding.UTF8.GetBytes(answer);
            var reply = $"HTTP/1.1 {code} X\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
            await s.WriteAsync(bytes);
        }

        public void Dispose() { try { _l?.Stop(); } catch { } }
    }

    // ---- the platform -------------------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Secrets(string key, string phrase) => new() { ["RSP_PAYFAST_KEY"] = key, ["RSP_PAYFAST_PASSPHRASE"] = phrase };

    private static Process StartPlatform()
    {
        var psi = Psi(null);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> Cli(Dictionary<string, string>? env, params string[] args)
    {
        var psi = Psi(env);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi(Dictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        if (!_realSandbox) psi.Environment["RSP_Testing__PayfastHost"] = Fake.Base;
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    // ---- small things ----------------------------------------------------------------------------------------------------------

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"payfastcheck: {_pass} of {_pass + _fail} passed ({HarnessDb.Name})");
        return _fail == 0 ? 0 : 1;
    }

    private static bool Contains(byte[] hay, string needle)
    {
        var n = Encoding.UTF8.GetBytes(needle);
        for (int i = 0; i + n.Length <= hay.Length; i++) if (hay.AsSpan(i, n.Length).SequenceEqual(n)) return true;
        return false;
    }

    private static string First(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("info:") && !l.StartsWith("warn:") && !l.Contains("Microsoft.") && !l.Contains("The query uses")) ?? "";
    private static string Last(string s) => s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
    private static string Text(string html)
    {
        var t = Regex.Replace(Regex.Replace(html, "(?s)<head.*?</head>|(?s)<script.*?</script>|(?s)<nav.*?</nav>", " "), "<[^>]+>", " ");
        t = Regex.Replace(WebUtility.HtmlDecode(t), "\\s+", " ").Trim();
        return t.Length <= 400 ? t : t[..400];
    }

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    private static HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(60) };

    private static async Task<string> Html(HttpClient c, string path) => await (await c.GetAsync(path)).Content.ReadAsStringAsync();

    private static async Task<string> Post(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return await r.Content.ReadAsStringAsync();
    }

    private static async Task<string> PostFollow(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return r.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
    }

    /// <summary>The page's own form for that handler, its own token — never one borrowed from another form (§80).</summary>
    private static async Task<string> PortalPost(HttpClient c, string page, string handler, params (string k, string v)[] fields)
    {
        var html = await Html(c, page);
        var at = html.IndexOf($"handler={handler}\"", StringComparison.Ordinal);
        var tokenFrom = at < 0 ? html : html[at..html.IndexOf("</form>", at, StringComparison.Ordinal)];
        var r = await c.PostAsync($"{page}?handler={handler}", Form(fields.Append(("__RequestVerificationToken", Token(tokenFrom))).ToArray()));
        return await r.Content.ReadAsStringAsync();
    }

    private static string Token(string page) => Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    private static string Snip(string html, string near) { var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase); return i < 0 ? "" : Regex.Replace(html.Substring(Math.Max(0, i - 60), Math.Min(260, html.Length - Math.Max(0, i - 60))), "\\s+", " "); }
    private static FormUrlEncodedContent Form(params (string k, string v)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    private static async Task<bool> WaitForPort(int port, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
            catch { await Task.Delay(400); }
        }
        return false;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
