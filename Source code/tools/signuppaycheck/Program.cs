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
// signuppaycheck — choosing a package and paying, PayFast stage B (2026-10-13, docs/NEXT-SESSION-PAYFAST.md, roadmap 4.8).
//
// A real throwaway platform with signups OFF (as live), a fake PayFast (payfastcheck's) and a fake mail server.
//
//   THE PREVIEW     signup is closed to the public; an admin's /owner/preview opens it to THAT browser only, and a
//                   hand-made cookie opens nothing.
//   SIGNING UP      a full-version company in South Africa ends at PayFast: R0.00 now, R500 a month from 14 days on (the
//                   company's own date), the owner's name and email; the company waits, unable to sign in, until PayFast's
//                   notice — then it is Active by itself, logged as PayFast, and the owner is emailed it is ready.
//                   Back from PayFast without a card: "not added", and the card again on a NEW checkout. The return page
//                   asks /api/payfast/checkout/{ref}, which knows only that state.
//   THE BASIC ONE   (the free version until 2026-10-25) on to PayFast too: R0.00 now, R50.00 from 14 days on; not approved by hand.
//   BILLING         an owner of a company on trial adds its card; an office administrator cannot; a company paying by card
//                   sees the amount and the next charge, and is not offered a second card.
//   UPGRADE         free → full goes straight on to PayFast, first charge when the 14-day trial ends.
//
//   dotnet run -c Release --project tools/signuppaycheck/signuppaycheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class SignupPayCheck
{
    private static int _pass, _fail;
    private static string _proj = "", _dataDir = "", _base = "";
    private static readonly string AdminKey = Hex(24);
    private const string Merchant = "10000999", MerchantKey = "fakekey12345", Passphrase = "Harness-Salt-2026", Pw = "Harness-Pass!2026x";
    private const string PayfastIp = "197.97.145.150";
    private static readonly FakePayfast Fake = new();
    private static readonly FakeSmtp Mail = new();
    private static readonly TimeZoneInfo Sa = TimeZoneInfo.CreateCustomTimeZone("SAST", TimeSpan.FromHours(2), "SAST", "SAST");

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
        Fake.Start(); Mail.Start();
        _dataDir = Path.Combine(Path.GetTempPath(), $"rs-signuppaycheck-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(_dataDir);
        int port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        Console.WriteLine("signuppaycheck — choosing a package and paying (PayFast stage B)");
        Console.WriteLine($"platform on {port} ({HarnessDb.Name}), a fake PayFast on {Fake.Port}, a mail server on {Mail.Port}");

        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var buildOut = await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }

        var mailSet = await Cli(null, "platform-mail", "set", "--host", "127.0.0.1", "--port", Mail.Port.ToString(), "--nossl", "--from", "noreply@pay.test", "--from-name", "The Remote Support Program");
        if (!mailSet.Contains("Saved")) { Console.WriteLine("Could not set the mailbox:\n" + mailSet); return 1; }
        var pf = await Cli(new() { ["RSP_PAYFAST_KEY"] = MerchantKey, ["RSP_PAYFAST_PASSPHRASE"] = Passphrase }, "payfast", "set", "--merchant-id", Merchant, "--sandbox");
        if (!pf.Contains("Saved")) { Console.WriteLine("Could not set PayFast:\n" + pf); return 1; }
        // A free company to upgrade, and a full one on trial that has no card yet.
        await Cli(null, "create-tenant", "Free Co", "free-co", "5");
        await Cli(null, "set-brand", "free-co", "plan", "basic");
        await Cli(null, "set-brand", "free-co", "status", "Active");
        await Cli(null, "add-tech", "free-co", "fay@free.test", Pw, "Fay Free", "--owner", "--technician");
        await Cli(null, "create-tenant", "Trial Co", "trial-co", "5");
        await Cli(null, "set-brand", "trial-co", "status", "Trial");
        await Cli(null, "add-tech", "trial-co", "tom@trial.test", Pw, "Tom Trial", "--owner", "--admin");
        await Cli(null, "add-tech", "trial-co", "olive@trial.test", Pw, "Olive Office", "--billing");

        var platform = StartPlatform();
        try
        {
            if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var admin = NewBrowser();
            var step = await Post(admin, "/owner/login", await Html(admin, "/owner/login"), ("Key", AdminKey));
            var secret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            if (await PostFollow(admin, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(secret))) != "/owner")
            { Console.WriteLine("Could not sign in to /owner."); return Done(); }

            // ---- the preview ---------------------------------------------------------------------------------------------
            Head("the preview: signup closed to the public, open to an admin's browser");
            var stranger = NewBrowser();
            Check("a stranger's /signup: invitation only", (await Html(stranger, "/signup")).Contains("Invitation only"));
            var forged = NewBrowser(); forged.DefaultRequestHeaders.Add("Cookie", "rsp_preview=made-up");
            Check("a hand-made preview cookie opens nothing", (await Html(forged, "/signup")).Contains("Invitation only"));
            Check("the admin's own browser: preview off to start with", (await Html(admin, "/owner/preview")).Contains("data-on=\"no\""));
            var on = await PortalPost(admin, "/owner/preview", null, ("on", "true"));
            Check("turned on, for this browser", on.Contains("data-on=\"yes\"") && on.Contains("Everybody else still sees the holding page"));
            Check("its /signup is open: where is your company", !(await Html(admin, "/signup")).Contains("Invitation only"));
            var pricing = WebUtility.HtmlDecode(await Html(admin, "/pricing"));
            Check("pricing: the card at signup, nothing for 14 days, no VAT", pricing.Contains("nothing is charged for 14 days") && pricing.Contains("No VAT is charged") && !pricing.Contains("VAT excluded"));
            Check("and the stranger's still is not", (await Html(stranger, "/signup")).Contains("Invitation only"));

            // ---- a full signup -----------------------------------------------------------------------------------------------
            Head("a full-version signup in South Africa");
            var (form, details) = await SignUp(admin, "full", "Pay Co", "Paula Payer", "paula@payco.test");
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Sa).Date;
            string V(string k) => form.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            Check("made, and straight on to PayFast", form.action == Fake.Base + "/eng/process", form.action.Length > 0 ? form.action : Snip(details, "alert") + " | " + Snip(details, "<h1"));
            Check("R0.00 now, R500.00 a month, monthly, until cancelled",
                V("amount") == "0.00" && V("recurring_amount") == "500.00" && V("frequency") == "3" && V("cycles") == "0" && V("subscription_type") == "1",
                $"{V("amount")} {V("recurring_amount")} f{V("frequency")} c{V("cycles")}");
            Check("the first charge 14 days from today, South Africa's date", V("billing_date") == today.AddDays(14).ToString("yyyy-MM-dd"), V("billing_date"));
            Check("the owner's name and email go to PayFast", V("name_first") == "Paula" && V("name_last") == "Payer" && V("email_address") == "paula@payco.test");
            var ref1 = V("m_payment_id");
            Check("PayFast sends them back to /signup/paid with the reference", V("return_url") == $"{_base}/signup/paid?ref={ref1}" && V("cancel_url") == $"{_base}/signup/paid?ref={ref1}&cancelled=1");
            Check("signed as PayFast checks (the checkout's rule)", V("signature") == CheckoutSignature(form.fields.SkipLast(1), Passphrase));
            var stateJson = await Html(stranger, $"/api/payfast/checkout/{ref1}");
            Check("the return page's question: waiting", stateJson.Contains("\"state\":\"waiting\""), stateJson);
            var unknown = await stranger.GetAsync("/api/payfast/checkout/rsp-00000000000000000000");
            Check("a reference that is not ours: 404, nothing said", unknown.StatusCode == HttpStatusCode.NotFound);
            Check("the return page: waiting for PayFast", (await Html(stranger, $"/signup/paid?ref={ref1}")).Contains("data-state=\"waiting\""));
            Check("the owner cannot sign in yet", (await SignIn("paula@payco.test")).landed != "/portal");
            var payRow = Row(await Html(admin, "/owner/approvals"), "data-slug=\"pay-co\"");
            Check("Approvals shows it waiting for the card at PayFast, with no Approve", payRow.Contains("Waiting for the card at PayFast") && !payRow.Contains("value=\"approve\""));
            var forcedApprove = WebUtility.HtmlDecode(await PortalPost(admin, "/owner/approvals", "Act", ("server", Regex.Match(payRow, "name=\"server\" value=\"(\\d+)\"").Groups[1].Value),
                ("id", V("custom_int1")), ("act", "approve")));
            Check("and approving it anyway is refused — a person must not switch on a company that has not paid", forcedApprove.Contains("waiting for its card at PayFast"), Snip(forcedApprove, "alert-bad"));

            Head("back from PayFast without a card");
            var back = WebUtility.HtmlDecode(await Html(stranger, $"/signup/paid?ref={ref1}&cancelled=1"));
            Check("\"Your card was not added\", nothing charged, a button to add it", back.Contains("Your card was not added") && back.Contains("Add your card"));
            var again = CheckoutForm(await PortalPost(stranger, $"/signup/paid?ref={ref1}&cancelled=1", null));
            string A(string k) => again.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            var ref2 = A("m_payment_id");
            Check("the card again: a NEW checkout for the same company", ref2.Length > 0 && ref2 != ref1 && A("custom_int1") == V("custom_int1") && A("recurring_amount") == "500.00", $"{ref1} → {ref2}");

            Head("PayFast confirms the card");
            var before = Mail.All.LastOrDefault();
            var (code, said) = await Notify(Notice(ref2, "PF-100", "0.00", "tok-pay", V("billing_date")));
            Check("its notice: recorded", said == "recorded", said);
            Check("the company is switched on by itself: ready", (await Html(stranger, $"/api/payfast/checkout/{ref2}")).Contains("\"state\":\"ready\""));
            var ready = await Mail.WaitFor("paula@payco.test", "your account is ready", before);
            Check("the owner is emailed: ready, the sign-in address, nothing until the first charge, R500 a month",
                ready != null && ready.Body.Contains($"{_base}/login") && ready.Body.Contains("Nothing is charged until " + today.AddDays(14).ToString("d MMMM yyyy", CultureInfo.InvariantCulture)) && ready.Body.Contains("R500 a month"),
                ready?.Subject ?? "nothing");
            var (owner, landed) = await SignIn("paula@payco.test");
            Check("the owner signs in now", landed == "/portal" || landed.StartsWith("/portal"), landed);
            var billing = WebUtility.HtmlDecode(await Html(owner, "/portal/billing"));
            Check("Billing: by card through PayFast, R500 a month, the next charge date", billing.Contains("id=\"cardOn\"") && billing.Contains("R500 a month") && billing.Contains(today.AddDays(14).ToString("yyyy-MM-dd")));
            Check("and no second card offered", !billing.Contains("Add your card"));
            var twice = WebUtility.HtmlDecode(await PortalPost(owner, "/portal/billing", "Card"));
            Check("a second card refused", twice.Contains("already paying by card"), Snip(twice, "alert-bad"));
            var log = WebUtility.HtmlDecode(await Html(admin, "/owner/log"));
            Check("the admin log: PayFast switched it on", Regex.IsMatch(log, "PayFast</td><td>Status Pending → Active</td><td>Pay Co"));

            // ---- stage C: the amount kept true --------------------------------------------------------------------------------
            // Since 2026-10-22 (§153) people never change the price: five seats and no more, R500 however many are in them. What
            // stage C still keeps true is a subscription agreed when extra seats existed — R800 for six — now owed R500.
            Head("stage C: PayFast is told the price — five seats, whoever fills them");
            var since = DateTime.UtcNow;
            // Four more people on the command line (another process, so only the sweep could notice): five seats, the most.
            for (int i = 1; i <= 4; i++) await Cli(null, "add-tech", "pay-co", $"tech{i}@payco.test", Pw, $"Tech {i}", "--technician");
            var sixth = await Cli(null, "add-tech", "pay-co", "tech5@payco.test", Pw, "Tech 5", "--technician");
            Check("a sixth person is refused — no more seats are allowed this company", sixth.Contains("may have 5 seats"), sixth.Trim());
            Check("...and five people send PayFast nothing: the price is still the package", await Fake.UpdateFor("tok-pay", null, since, 8) == null);
            var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RemoteSupport.Platform.Data.PlatformDb>()
                .UseHarnessDb($"Data Source={Path.Combine(_dataDir, "platform.db")};Pooling=False").Options;
            async Task AgreedWithAnExtraSeat()
            {
                await using var db = new RemoteSupport.Platform.Data.PlatformDb(dbOptions);
                var sub = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PayfastSubscriptions, s => s.Token == "tok-pay");
                sub.AmountCents = 80000;
                await db.SaveChangesAsync();
            }
            await AgreedWithAnExtraSeat();
            since = DateTime.UtcNow;
            var up500 = await Fake.UpdateFor("tok-pay", "50000", since, 40);
            Check("a subscription agreed at R800 (an extra seat, before 22 October): the sweep tells PayFast R500.00 — in CENTS, signed the API's way",
                up500 is { signed: true }, up500 == null ? "nothing sent" : $"{up500.Value.amount} cents");
            await Task.Delay(500);
            var b500 = WebUtility.HtmlDecode(await Html(owner, "/portal/billing"));
            Check("Billing: R500 a month by card, no warning", Regex.IsMatch(b500, "id=\"cardOn\"[\\s\\S]*R500 a month") && !b500.Contains("id=\"amountPending\""));
            Check("the admin log: PayFast, R800 → R500 from the next charge", WebUtility.HtmlDecode(await Html(admin, "/owner/log")).Contains("Monthly amount R800 → R500 from the next charge"));

            await AgreedWithAnExtraSeat();
            Fake.RefuseUpdates = true;
            since = DateTime.UtcNow;
            var people = await Html(owner, "/portal/technicians");
            await owner.PostAsync("/portal/technicians?handler=Remove", Form(("email", "tech4@payco.test"), ("__RequestVerificationToken", Token(people))));
            var refused = await Fake.UpdateFor("tok-pay", "50000", since, 10);
            Check("removing a person in the portal tells PayFast AT ONCE (not waiting for the sweep): R500.00", refused != null && (refused.Value.at - since).TotalSeconds < 5,
                refused == null ? "nothing sent" : $"{(refused.Value.at - since).TotalSeconds:0.0} s");
            await Task.Delay(500);
            var bPending = WebUtility.HtmlDecode(await Html(owner, "/portal/billing"));
            Check("PayFast refused it: Billing says the new price and that PayFast has not taken it yet", bPending.Contains("id=\"amountPending\"") && bPending.Contains("R500 a month, and PayFast has not yet taken"), Snip(bPending, "amountPending"));
            Check("and the owner's PayFast page says so on the subscription", (await Html(admin, "/owner/payfast")).Contains("data-amount-problem=\"yes\""));
            Fake.RefuseUpdates = false;
            since = DateTime.UtcNow;
            var retried = await Fake.UpdateFor("tok-pay", "50000", since, 40);
            await Task.Delay(500);
            var bFixed = WebUtility.HtmlDecode(await Html(owner, "/portal/billing"));
            Check("the sweep tries again, PayFast takes it, and the warning goes", retried != null && !bFixed.Contains("id=\"amountPending\"") && Regex.IsMatch(bFixed, "id=\"cardOn\"[\\s\\S]*R500 a month"));

            Head("stage C: a payment, and our receipt");
            before = Mail.All.LastOrDefault();
            (code, said) = await Notify(Notice(ref2, "PF-101", "500.00", "tok-pay", today.AddMonths(1).AddDays(14).ToString("yyyy-MM-dd")));
            var receipt = await Mail.WaitFor("paula@payco.test", "payment received, R500", before);
            Check("a renewal of R500.00: recorded, and the owner gets OUR receipt — PayFast's reference, the next charge, no VAT",
                said == "recorded" && receipt != null && receipt.Body.Contains("PF-101") && receipt.Body.Contains("No VAT is charged") && receipt.Body.Contains("The next charge: " + today.AddMonths(1).AddDays(14).ToString("yyyy-MM-dd")),
                receipt?.Subject ?? said);
            var bPaid = await Html(owner, "/portal/billing");
            Check("Billing lists the payment", bPaid.Contains("id=\"cardPayments\"") && bPaid.Contains("data-pf=\"PF-101\""));
            Check("the R0.00 that only checked the card is not listed as a payment", !bPaid.Contains("data-pf=\"PF-100\""));

            // ---- the free version --------------------------------------------------------------------------------------------
            // The Basic version (the free version until 2026-10-25, HANDOVER §161): R50 a month, so it goes on to PayFast too.
            Head("the Basic version: R50 a month, on to PayFast like the full version");
            var (freeForm, freeDone) = await SignUp(admin, "basic", "", "Frank Free", "frank@free.test");
            string F(string k) => freeForm.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            Check("made, and straight on to PayFast (it used to wait for approval, free)", freeForm.action == Fake.Base + "/eng/process",
                freeForm.action.Length > 0 ? freeForm.action : Snip(freeDone, "alert") + " | " + Snip(freeDone, "<h1"));
            Check("R0.00 now, R50.00 a month from 14 days on, monthly, until cancelled",
                F("amount") == "0.00" && F("recurring_amount") == "50.00" && F("billing_date") == today.AddDays(14).ToString("yyyy-MM-dd") && F("frequency") == "3" && F("cycles") == "0",
                $"{F("amount")} {F("recurring_amount")} {F("billing_date")}");
            var appr = await Html(admin, "/owner/approvals");
            var row = Section(appr, "data-slug=\"frank-free\"");
            Check("Approvals shows it waiting for the card at PayFast, with no Approve", row.Contains("Waiting for the card at PayFast") && !row.Contains("value=\"approve\""));
            var approved = await PortalPost(admin, "/owner/approvals", "Act", ("server", Regex.Match(row, "name=\"server\" value=\"(\\d+)\"").Groups[1].Value),
                ("id", Regex.Match(row, "name=\"id\" value=\"(\\d+)\"").Groups[1].Value), ("act", "approve"));
            Check("...and approving it by hand anyway is refused — PayFast switches it on", approved.Contains("is waiting for its card at PayFast"), Snip(approved, "alert"));

            // ---- billing: a company on trial adds its card ---------------------------------------------------------------------
            Head("Billing: a company on trial adds its card");
            var (olive, _) = await SignIn("olive@trial.test");
            var ob = WebUtility.HtmlDecode(await Html(olive, "/portal/billing"));
            Check("the office administrator sees card payment, but the owner adds it", ob.Contains("id=\"cardOff\"") && ob.Contains("The owner adds the company's card") && !ob.Contains(">Add your card<"));
            var oliveTry = WebUtility.HtmlDecode(await olive.PostAsync("/portal/billing?handler=Card", Form(("__RequestVerificationToken", Token(ob)))).Result.Content.ReadAsStringAsync());
            Check("and posting it anyway is refused", oliveTry.Contains("Only the owner can add the company's card"));
            var (tom, _) = await SignIn("tom@trial.test");
            var tb = await Html(tom, "/portal/billing");
            Check("the owner sees Add your card", tb.Contains(">Add your card<"));
            var card = CheckoutForm(await PortalPost(tom, "/portal/billing", "Card"));
            string C(string k) => card.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            Check("on to PayFast: R0.00 now, R500.00 from tomorrow (a trial with no end date)", C("amount") == "0.00" && C("recurring_amount") == "500.00" && C("billing_date") == today.AddDays(1).ToString("yyyy-MM-dd"), C("billing_date"));
            (code, said) = await Notify(Notice(C("m_payment_id"), "PF-200", "0.00", "tok-trial", C("billing_date")));
            var tb2 = WebUtility.HtmlDecode(await Html(tom, "/portal/billing"));
            Check("confirmed: the company is Active and paying by card", said == "recorded" && tb2.Contains("id=\"cardOn\"") && tb2.Contains(">Active<"), said);

            // ---- upgrade -----------------------------------------------------------------------------------------------------
            Head("upgrade: free to full, then the card");
            var (fay, _) = await SignIn("fay@free.test");
            var up = await Html(fay, "/portal/upgrade");
            var upForm = CheckoutForm(await PortalPost(fay, "/portal/upgrade", null, ("Accept", "true"), ("TermsSha", Field(up, "TermsSha")), ("Agreed", Field(up, "Agreed"))));
            string U(string k) => upForm.fields.FirstOrDefault(f => f.Key == k).Value ?? "";
            Check("upgraded and on to PayFast: first charge when the 14-day trial ends", U("recurring_amount") == "500.00" && U("billing_date") == today.AddDays(14).ToString("yyyy-MM-dd"), U("billing_date"));
            before = Mail.All.LastOrDefault();
            (code, said) = await Notify(Notice(U("m_payment_id"), "PF-300", "0.00", "tok-fay", U("billing_date")));
            var fayMail = await Mail.WaitFor("fay@free.test", "your card is in", before);
            Check("confirmed: Active, and the owner is told the card is in", said == "recorded" && fayMail != null && fayMail.Body.Contains("subscription is active"), fayMail?.Subject ?? said);

            // ---- stage D: payments that did not come ------------------------------------------------------------------------
            // PayFast sends nothing for a failed renewal; a charge date in the past with no payment since IS the failure. Each
            // company's card is confirmed with a notice whose billing_date is that many days ago.
            Head("stage D: a payment that did not come — 14 days' grace");
            before = Mail.All.LastOrDefault();
            var late1 = await CardCompany("late1", "One Day Co", "lou@late1.test", today.AddDays(-1), "tok-late1");
            var late7 = await CardCompany("late7", "Week Co", "wes@late7.test", today.AddDays(-7), "tok-late7");
            var late15 = await CardCompany("late15", "Gone Quiet Co", "gus@late15.test", today.AddDays(-15), "tok-late15");
            Fake.States["tok-moved"] = ("ACTIVE", today.AddDays(27).ToString("yyyy-MM-dd"));
            var moved = await CardCompany("moved", "Moved On Co", "mo@moved.test", today.AddDays(-3), "tok-moved");
            var r1 = await Mail.WaitFor("lou@late1.test", "your payment did not go through", before, seconds: 40);
            Check("a day late: the owner is told, with PayFast's change-the-card link and the day it would be suspended",
                r1 != null && r1.Body.Contains("/eng/recurring/update/tok-late1") && r1.Body.Contains(today.AddDays(14).ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                r1?.Subject ?? "nothing");
            var r7 = await Mail.WaitFor("wes@late7.test", "your payment is a week late", before, seconds: 20);
            Check("a week late: the week's reminder — and only that one", r7 != null && Mail.All.Count(m => m.To.Contains("wes@late7.test") && m.Subject.Contains("did not go through")) == 0, r7?.Subject ?? "nothing");
            var lb = WebUtility.HtmlDecode(await Html(late1.owner, "/portal/billing"));
            Check("Billing says the payment has not come, and when it was due", lb.Contains("id=\"paymentLate\"") && lb.Contains(today.AddDays(-1).ToString("yyyy-MM-dd")));
            var s15 = await Mail.WaitFor("gus@late15.test", "suspended - payment not received", before, seconds: 20);
            Check("more than 14 days late: SUSPENDED, the owner told why, with the card link", s15 != null && s15.Body.Contains("/eng/recurring/update/tok-late15"), s15?.Subject ?? "nothing");
            Check("…and only that email — not the generic suspension notice as well", Mail.All.Count(m => m.To.Contains("gus@late15.test") && m.Subject.Contains("has been suspended")) == 0);
            Check("its people cannot sign in", (await SignIn("gus@late15.test")).landed != "/portal");
            var all = WebUtility.HtmlDecode(await Html(admin, "/owner/tenants"));
            Check("All companies says it: unpaid since the date, suspended", all.Contains($"data-payment=\"Card, unpaid since {today.AddDays(-15):yyyy-MM-dd} (suspended)\""));
            log = WebUtility.HtmlDecode(await Html(admin, "/owner/log"));
            Check("the log: suspended by PayFast, and the reminders", log.Contains("Status Active → Suspended</td><td>Gone Quiet Co") && log.Contains("reminder 2 of 3 sent</td><td>Week Co"));
            for (int i = 0; i < 40 && !log.Contains("PayFast had moved on"); i++) { await Task.Delay(1000); log = WebUtility.HtmlDecode(await Html(admin, "/owner/log")); }
            Check("PayFast had moved on (its notice lost): NOT late, and the log says so", log.Contains($"PayFast had moved on to {today.AddDays(27):yyyy-MM-dd}") &&
                Mail.All.All(m => !m.To.Contains("mo@moved.test") || !m.Subject.Contains("payment")),
                string.Join(" / ", Mail.All.Where(m => m.To.Contains("mo@moved.test")).Select(m => m.Subject)) + " | " + Regex.Match(log, "[^>]*Moved On Co").Value + " | " + Regex.Match(log, "PayFast had moved[^<]*").Value);

            Head("stage D: the payment comes");
            before = Mail.All.LastOrDefault();
            (code, said) = await Notify(Notice(late15.reference, "PF-L15", "500.00", "tok-late15", today.AddDays(15).ToString("yyyy-MM-dd")));
            var back15 = await Mail.WaitFor("gus@late15.test", "payment received - your service is back", before, seconds: 15);
            Check("paid at last: the suspension WE made is lifted, and they are told", said == "recorded" && back15 != null, back15?.Subject ?? said);
            Check("its people sign in again", (await SignIn("gus@late15.test")).landed.StartsWith("/portal"));
            // An admin's suspension is theirs: a payment must not lift it.
            var tenantsPage = await Html(admin, "/owner/tenants");
            var oneRow = Section(tenantsPage, "data-slug=\"late1\"");
            await PortalPost(admin, "/owner/tenants", "Act", ("server", Regex.Match(oneRow, "name=\"server\" value=\"(\\d+)\"").Groups[1].Value),
                ("id", Regex.Match(oneRow, "name=\"id\" value=\"(\\d+)\"").Groups[1].Value), ("act", "status"), ("status", "Suspended"));
            (code, said) = await Notify(Notice(late1.reference, "PF-L1", "500.00", "tok-late1", today.AddDays(29).ToString("yyyy-MM-dd")));
            await Task.Delay(1000);
            Check("a company an ADMIN suspended stays suspended when its payment comes", said == "recorded" && (await SignIn("lou@late1.test")).landed != "/portal");

            Head("stage D: cancelling");
            var payBill = await Html(owner, "/portal/billing");
            var unsure = WebUtility.HtmlDecode(await PortalPost(owner, "/portal/billing", "Cancel"));
            Check("Cancel without ticking the box: nothing happens", unsure.Contains("Tick the box") && Fake.Cancels.Count == 0);
            before = Mail.All.LastOrDefault();
            var nextRun = today.AddMonths(1).AddDays(14);
            var cancelled = WebUtility.HtmlDecode(await PortalPost(owner, "/portal/billing", "Cancel", ("sure", "true")));
            Check("Cancel, ticked: PayFast is told (signed), and the service runs to the day before the next charge",
                Fake.Cancels.Any(c => c.token == "tok-pay" && c.signed) && cancelled.Contains($"carries on until the end of {nextRun.AddDays(-1):yyyy-MM-dd}"), Snip(cancelled, "alert"));
            var cMail = await Mail.WaitFor("paula@payco.test", "subscription cancelled", before, seconds: 10);
            Check("the owner is emailed it, with the last day", cMail != null && cMail.Body.Contains(nextRun.AddDays(-1).ToString("d MMMM yyyy", CultureInfo.InvariantCulture)), cMail?.Subject ?? "nothing");
            var afterCancel = WebUtility.HtmlDecode(await Html(owner, "/portal/billing"));
            Check("Billing: cancelled, until that day, and Add your card offered again", afterCancel.Contains("id=\"cardEnding\"") && afterCancel.Contains(">Add your card<"));
            (code, said) = await Notify(CancelledNotice(ref2, "PF-100", "tok-pay"));
            await Task.Delay(500);
            Check("PayFast's own CANCELLED notice then: recorded, and no second email", said == "recorded" && Mail.All.Count(m => m.To.Contains("paula@payco.test") && m.Subject.Contains("subscription cancelled")) == 1, said);
            var again2 = CheckoutForm(await PortalPost(owner, "/portal/billing", "Card"));
            Check("changed their mind: the new card is first charged on the date the old one would have been — nothing paid twice",
                again2.fields.FirstOrDefault(f => f.Key == "billing_date").Value == nextRun.ToString("yyyy-MM-dd"), again2.fields.FirstOrDefault(f => f.Key == "billing_date").Value);

            Head("stage D: the end");
            before = Mail.All.LastOrDefault();
            var ending = await CardCompany("ending", "Ending Co", "ed@ending.test", today.AddDays(-2), "tok-ending");
            (code, said) = await Notify(CancelledNotice(ending.reference, "PF-E0", "tok-ending"));
            var ended = await Mail.WaitFor("ed@ending.test", "your service has ended", before, seconds: 40);
            Check("cancelled at PayFast with its charge already missed: closed by the sweep, and told", ended != null, ended?.Subject ?? said);
            Check("no \"carries on until\" email for a day gone by", Mail.All.All(m => !m.To.Contains("ed@ending.test") || !m.Subject.Contains("subscription cancelled")));
            Check("its people cannot sign in", (await SignIn("ed@ending.test")).landed != "/portal");
        }
        finally { try { platform.Kill(true); } catch { } }
        Fake.Dispose(); Mail.Dispose();
        return Done();
    }

    // ---- signing up and signing in ------------------------------------------------------------------------------------

    /// <summary>Through the signup pages as a person would: the region and plan, the terms, the company. The PayFast form, if any.</summary>
    private static async Task<((string action, List<KeyValuePair<string, string>> fields) form, string page)> SignUp(HttpClient c, string plan, string company, string name, string email)
    {
        var terms = await Html(c, $"/signup?region=ZA&plan={plan}");
        var sha = Field(terms, "TermsSha");
        // Each step posted with the token of the page it is on, as a browser does.
        var details = await (await c.PostAsync("/signup?handler=Terms", Form(("Region", "ZA"), ("Plan", plan), ("TermsSha", sha), ("Accept", "true"),
            ("__RequestVerificationToken", Token(terms))))).Content.ReadAsStringAsync();
        var made = await (await c.PostAsync("/signup?handler=Create", Form(("Region", "ZA"), ("Plan", plan), ("TermsSha", sha), ("Accept", "true"),
            ("CompanyName", company), ("ContactName", name), ("ContactNumber", "0821234567"), ("Email", email), ("Password", Pw),
            ("__RequestVerificationToken", Token(details))))).Content.ReadAsStringAsync();
        return (CheckoutForm(made, quiet: true), WebUtility.HtmlDecode(made));
    }

    /// <summary>
    /// A company paying by card whose card was confirmed with a charge date of <paramref name="due"/>: made on the command
    /// line (on trial), its owner adds the card on Billing, and PayFast's notice says the next charge is that date.
    /// </summary>
    private static async Task<(HttpClient owner, string reference)> CardCompany(string slug, string name, string email, DateTime due, string token)
    {
        await Cli(null, "create-tenant", name, slug, "5");
        await Cli(null, "set-brand", slug, "status", "Trial");
        await Cli(null, "add-tech", slug, email, Pw, name.Split(' ')[0] + " Owner", "--owner", "--admin");
        var (owner, _) = await SignIn(email);
        var form = CheckoutForm(await PortalPost(owner, "/portal/billing", "Card"));
        var reference = form.fields.FirstOrDefault(f => f.Key == "m_payment_id").Value ?? "";
        var (_, said) = await Notify(Notice(reference, "PF-" + slug, "0.00", token, due.ToString("yyyy-MM-dd")));
        if (said != "recorded") Console.WriteLine($"        {slug}: the card notice said {said}");
        return (owner, reference);
    }

    private static List<KeyValuePair<string, string>> CancelledNotice(string reference, string pfId, string token)
    {
        var f = Notice(reference, pfId, "0.00", token, "").Where(x => x.Key != "signature")
            .Select(x => x.Key == "payment_status" ? new KeyValuePair<string, string>(x.Key, "CANCELLED") : x).ToList();
        var param = string.Join("&", f.Select(x => x.Key + "=" + PhpUrlencode(x.Value)));
        f.Add(new("signature", Md5(param + "&passphrase=" + PhpUrlencode(Passphrase))));
        return f;
    }

    private static async Task<(HttpClient c, string landed)> SignIn(string email)
    {
        var c = NewBrowser();
        var form = await Html(c, "/login");
        var r = await c.PostAsync("/login", Form(("Email", email), ("Password", Pw), ("__RequestVerificationToken", Token(form))));
        return (c, r.RequestMessage?.RequestUri?.AbsolutePath ?? "?");
    }

    // ---- PayFast, as payfastcheck has it ----------------------------------------------------------------------------------

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

    /// <summary>PayFast's API rule: headers and body with the passphrase, sorted alphabetically, blanks out.</summary>
    private static string ApiSignature(Dictionary<string, string> values, string phrase)
    {
        var all = new SortedDictionary<string, string>(values.Where(v => v.Value.Length > 0).ToDictionary(v => v.Key, v => v.Value), StringComparer.Ordinal) { ["passphrase"] = phrase };
        return Md5(string.Join("&", all.Select(v => v.Key + "=" + PhpUrlencode(v.Value))));
    }

    private static List<KeyValuePair<string, string>> Notice(string reference, string pfId, string gross, string token, string billingDate)
    {
        var f = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", reference), new("pf_payment_id", pfId), new("payment_status", "COMPLETE"),
            new("item_name", "The Remote Support Program"), new("item_description", ""),
            new("amount_gross", gross), new("amount_fee", "0.00"), new("amount_net", gross),
            new("custom_str1", ""), new("custom_int1", ""), new("name_first", "Test"), new("name_last", "User 01"), new("email_address", "sbtu01@payfast.io"),
            new("merchant_id", Merchant), new("token", token), new("billing_date", billingDate),
        };
        var param = string.Join("&", f.Select(x => x.Key + "=" + PhpUrlencode(x.Value)));
        f.Add(new("signature", Md5(param + "&passphrase=" + PhpUrlencode(Passphrase))));
        return f;
    }

    private static async Task<(int code, string said)> Notify(List<KeyValuePair<string, string>> fields)
    {
        using var c = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var body = string.Join("&", fields.Select(x => x.Key + "=" + PhpUrlencode(x.Value)));
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/payfast/notify") { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        req.Headers.Add("X-Forwarded-For", PayfastIp);
        using var r = await c.SendAsync(req);
        return ((int)r.StatusCode, (await r.Content.ReadAsStringAsync()).Trim());
    }

    private static (string action, List<KeyValuePair<string, string>> fields) CheckoutForm(string html, bool quiet = false)
    {
        var i = html.IndexOf("id=\"toPayfast\"", StringComparison.Ordinal);
        if (i < 0) { if (!quiet) Console.WriteLine("        no checkout form: " + Snip(html, "alert")); return ("", new()); }
        var formEnd = html.IndexOf("</form>", i, StringComparison.Ordinal);
        var formTag = html[html.LastIndexOf("<form", i, StringComparison.Ordinal)..html.IndexOf('>', i)];
        var action = WebUtility.HtmlDecode(Regex.Match(formTag, "action=\"([^\"]*)\"").Groups[1].Value);
        var fields = Regex.Matches(html[i..formEnd], "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();
        foreach (Match m in Regex.Matches(html[i..formEnd], "<input name=\"([^\"]+)\" type=\"hidden\" value=\"([^\"]*)\""))
            fields.Add(new(m.Groups[1].Value, WebUtility.HtmlDecode(m.Groups[2].Value)));
        return (action, fields);
    }

    /// <summary>The confirmation answers VALID; the API ping is accepted. (payfastcheck tests both properly.)</summary>
    private sealed class FakePayfast : IDisposable
    {
        private TcpListener? _l;
        public int Port;
        public string Base => $"http://127.0.0.1:{Port}";

        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, 0); _l.Start(); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Serve(c); } });
        }

        /// <summary>Every amount update the platform sent: (token, amount in cents, signed right, when).</summary>
        public readonly List<(string token, string amount, bool signed, DateTime at)> Updates = new();
        /// <summary>While true, an update is refused as PayFast refuses one.</summary>
        public volatile bool RefuseUpdates;
        /// <summary>What the API says each subscription is: (status_text, run_date "yyyy-MM-dd" or ""). Unknown: ACTIVE, no date.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string state, string run)> States = new();
        /// <summary>Every cancellation the platform asked for: (token, signed right).</summary>
        public readonly List<(string token, bool signed)> Cancels = new();

        private async Task Serve(TcpClient c)
        {
            using var _ = c;
            var s = c.GetStream();
            var head = new StringBuilder();
            var buf = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n")) { if (await s.ReadAsync(buf) == 0) return; head.Append((char)buf[0]); }
            var lines = head.ToString().Split("\r\n");
            var method = lines[0].Split(' ')[0];
            var path = lines[0].Split(' ')[1];
            var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            var len = int.TryParse(headers.GetValueOrDefault("content-length"), out var n) ? n : 0;
            var b = new byte[len]; int got = 0;
            while (got < len) { var r = await s.ReadAsync(b.AsMemory(got)); if (r == 0) break; got += r; }
            var body = Encoding.UTF8.GetString(b, 0, got);
            int code = 200; string answer;
            if (path.StartsWith("/eng/query/validate")) answer = "VALID";
            else if (path.StartsWith("/ping")) answer = "\"API V1\"";
            else if (method == "PATCH" && path.StartsWith("/subscriptions/") && path.Contains("/update"))
            {
                var token = path.Split('/')[2];
                var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')).ToDictionary(p => p[0], p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
                var values = new Dictionary<string, string>(form)
                {
                    ["merchant-id"] = headers.GetValueOrDefault("merchant-id", ""), ["version"] = headers.GetValueOrDefault("version", ""), ["timestamp"] = headers.GetValueOrDefault("timestamp", ""),
                };
                bool signed = headers.GetValueOrDefault("signature") == ApiSignature(values, Passphrase);
                lock (Updates) Updates.Add((token, form.GetValueOrDefault("amount", ""), signed, DateTime.UtcNow));
                if (!signed) { code = 401; answer = "{\"code\":401,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"Merchant authorisation failed\"}}"; }
                else if (RefuseUpdates) { code = 400; answer = "{\"code\":400,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"The subscription is not in a valid state.\"}}"; }
                else answer = "{\"code\":200,\"status\":\"success\",\"data\":{\"response\":{\"token\":\"" + token + "\",\"amount\":" + form.GetValueOrDefault("amount", "0") + ",\"frequency\":3,\"status\":1}}}";
            }
            else if (method == "GET" && path.StartsWith("/subscriptions/") && path.Contains("/fetch"))
            {
                var token = path.Split('/')[2];
                var (state, run) = States.GetValueOrDefault(token, ("ACTIVE", ""));
                answer = "{\"code\":200,\"status\":\"success\",\"data\":{\"response\":{\"amount\":50000,\"cycles\":0,\"cycles_complete\":1,\"frequency\":3,"
                       + "\"run_date\":\"" + (run.Length > 0 ? run + "T00:00:00+02:00" : "") + "\",\"status\":" + (state == "ACTIVE" ? 1 : 2) + ",\"status_reason\":\"\",\"status_text\":\"" + state + "\",\"token\":\"" + token + "\"}}}";
            }
            else if (method == "PUT" && path.StartsWith("/subscriptions/") && path.Contains("/cancel"))
            {
                var token = path.Split('/')[2];
                var values = new Dictionary<string, string>
                {
                    ["merchant-id"] = headers.GetValueOrDefault("merchant-id", ""), ["version"] = headers.GetValueOrDefault("version", ""), ["timestamp"] = headers.GetValueOrDefault("timestamp", ""),
                };
                bool signed = headers.GetValueOrDefault("signature") == ApiSignature(values, Passphrase);
                lock (Cancels) Cancels.Add((token, signed));
                if (signed) { States[token] = ("CANCELLED", ""); answer = "{\"code\":200,\"status\":\"success\",\"data\":{\"response\":true}}"; }
                else { code = 401; answer = "{\"code\":401,\"status\":\"failed\",\"data\":{\"response\":false,\"message\":\"Merchant authorisation failed\"}}"; }
            }
            else { code = 404; answer = "not here"; }
            var bytes = Encoding.UTF8.GetBytes(answer);
            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} X\r\nContent-Type: text/plain\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await s.WriteAsync(bytes);
        }

        public async Task<(string token, string amount, bool signed, DateTime at)?> UpdateFor(string token, string? amount, DateTime after, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                lock (Updates) { var u = Updates.LastOrDefault(x => x.token == token && (amount == null || x.amount == amount) && x.at > after); if (u.token != null) return u; }
                await Task.Delay(200);
            }
            return null;
        }

        public void Dispose() { try { _l?.Stop(); } catch { } }
    }

    // ---- the platform ---------------------------------------------------------------------------------------------------

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
        psi.Environment["RSP_Platform__Region"] = "ZA";
        // As live: nobody signs up without the preview.
        psi.Environment["RSP_Platform__SignupsEnabled"] = "false";
        // A harness platform only — never a live server (HANDOVER §72).
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
        psi.Environment["RSP_Testing__PayfastHost"] = Fake.Base;
        // The PayFast sweep every 15 s (an hour on a real server), so a change made on the command line is caught quickly.
        psi.Environment["RSP_Platform__PayfastSweepSeconds"] = "15";
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    // ---- small things -------------------------------------------------------------------------------------------------

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"signuppaycheck: {_pass} of {_pass + _fail} passed ({HarnessDb.Name})");
        return _fail == 0 ? 0 : 1;
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

    /// <summary>The page's own form (for that handler, or with none, its first), its own token (§80).</summary>
    private static async Task<string> PortalPost(HttpClient c, string page, string? handler, params (string k, string v)[] fields)
    {
        var html = await Html(c, page);
        var at = handler == null ? html.IndexOf("<form method=\"post\"", StringComparison.Ordinal) : html.IndexOf($"handler={handler}\"", StringComparison.Ordinal);
        var tokenFrom = at < 0 ? html : html[at..html.IndexOf("</form>", at, StringComparison.Ordinal)];
        var path = handler == null ? page : $"{page}{(page.Contains('?') ? "&" : "?")}handler={handler}";
        var r = await c.PostAsync(path, Form(fields.Append(("__RequestVerificationToken", Token(tokenFrom))).ToArray()));
        return await r.Content.ReadAsStringAsync();
    }

    private static string Token(string page) => Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    private static string Row(string html, string near) { var i = html.IndexOf(near, StringComparison.Ordinal); return i < 0 ? "" : html[i..html.IndexOf("</tr>", i, StringComparison.Ordinal)]; }
    private static string Section(string html, string near) { var i = html.IndexOf(near, StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 3000)]; }
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

    // ---- a mail server (controlcheck's, from invitecheck) -------------------------------------------------------------------
    private sealed class FakeSmtp : IDisposable
    {
        private TcpListener? _l;
        private readonly List<Message> _mails = new();
        public int Port { get; private set; }
        public int Count { get { lock (_mails) return _mails.Count; } }
        public List<Message> All { get { lock (_mails) return _mails.ToList(); } }

        public void Start()
        {
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
        }

        private async Task Handle(TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                var r = new StreamReader(s, Encoding.UTF8);
                var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await w.WriteLineAsync("220 invitecheck ESMTP");
                var to = new List<string>();
                while (await r.ReadLineAsync() is { } line)
                {
                    var cmd = line.ToUpperInvariant();
                    if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await w.WriteLineAsync("250 invitecheck");
                    else if (cmd.StartsWith("RCPT TO:")) { to.Add(line[8..].Trim().Trim('<', '>').ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                    else if (cmd == "DATA")
                    {
                        await w.WriteLineAsync("354 go ahead");
                        var sb = new StringBuilder();
                        while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                        lock (_mails) _mails.Add(new Message(to.ToList(), sb.ToString()));
                        to.Clear();
                        await w.WriteLineAsync("250 queued");
                    }
                    else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                    else await w.WriteLineAsync("250 OK");
                }
            }
        }

        public async Task<Message?> WaitFor(string to, string subjectPart, Message? after = null, string? fromBy = null, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                List<Message> all; lock (_mails) all = _mails.ToList();
                var start = after == null ? 0 : all.IndexOf(after) + 1;
                var m = all.Skip(start).LastOrDefault(x => x.To.Contains(to) && x.Subject.Contains(subjectPart) && (fromBy == null || x.Body.Contains(fromBy)));
                if (m != null) return m;
                await Task.Delay(100);
            }
            return null;
        }

        public void Dispose() { try { _l?.Stop(); } catch { } }
    }

    /// <summary>One message, read as a mail program would: unfolded headers, decoded body.</summary>
    private sealed class Message
    {
        public List<string> To { get; }
        public string From = "", FromName = "", ReplyTo = "", Subject = "", Body = "";
        public Message(List<string> to, string raw)
        {
            To = to;
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = Regex.Replace(split < 0 ? raw : raw[..split], "\r\n[ \t]+", " ");
            var body = split < 0 ? "" : raw[(split + 4)..];
            string H(string n) => Regex.Match(head, $"^{n}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            From = Words(H("From")); ReplyTo = Words(H("Reply-To")); Subject = Words(H("Subject"));
            FromName = Regex.Match(From, "^\"?([^\"<]+?)\"?\\s*<").Groups[1].Value.Trim();
            var cte = H("Content-Transfer-Encoding").ToLowerInvariant();
            Body = cte == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, "\\s", "")))
                 : cte == "quoted-printable" ? QuotedPrintable(body) : body;
            Body = Body.Replace("\r\n", "\n");
        }


        private static string QuotedPrintable(string s)
        {
            s = s.Replace("=\r\n", "");
            var bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>RFC 2047 encoded words (=?utf-8?B?…?= and =?utf-8?Q?…?=) back to text.</summary>
        private static string Words(string v) => Regex.Replace(v, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
            m.Groups[2].Value.ToUpperInvariant() == "B"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[3].Value))
                : QuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
    }
}
