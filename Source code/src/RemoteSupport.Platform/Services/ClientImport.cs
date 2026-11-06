using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Turning somebody else's client list into ours — Clint, 2026-09-28: "one very important feature...
/// the ability to import clients from another program." What every IT company switching to us has,
/// and what it would otherwise retype.
///
/// The format is one row per contact person: Client, Contact person, Phone, Email, Address, Notes.
/// Only Client is required. Rows that name the same client become ONE client with several contacts,
/// which is how a company with three people at it goes in without a second sheet.
///
/// Three rules, each of which is a way this could quietly spoil the records it is meant to fill:
///
/// 1. <b>Nothing already on file is overwritten.</b> A client already here, matched by name the way
///    <see cref="ClientService.ByNameAsync"/> matches, only has its BLANK fields filled; a contact
///    already on it likewise. So the same file imported twice adds nothing the second time, and an
///    old export cannot undo a number somebody corrected by hand last week.
///
/// 2. <b>Nothing is saved until the person has seen what will happen.</b> Parse and plan are one
///    step, save is another, and the save plans AGAIN against the database as it is then — the
///    preview is a promise about the file, not a lock on the data.
///
/// 3. <b>Repairs are shown, never silent.</b> Letters garbled by an export (Ã© for é), and a phone
///    number that lost its leading zero to Excel, are put right — and the preview says so row by row.
///
/// Parsing is static and touches no database, so <c>tools/importcheck</c> can drive it with a sheet
/// it made up; <see cref="ClientImportService"/> is the half that reads and writes.
/// </summary>
public static class ClientImport
{
    public enum Field { Client, Contact, Phone, Email, Address, Notes }

    /// <summary>The template's headings, in order. The page shows these and the template carries them.</summary>
    public static readonly string[] TemplateHeaders = { "Client", "Contact person", "Phone", "Email", "Address", "Notes" };

    /// <summary>A file bigger than this is not a client list; refuse it rather than hold it in a form.</summary>
    public const int MaxRows = 5000;

    /// <summary>
    /// An address on this many different clients is a stand-in, not somebody's inbox — Clint's Xero
    /// export had the IT company's own address on 157 clients that had none. The preview lists every
    /// one of these, ticked to be left out, and the person may untick.
    /// </summary>
    public const int SharedEmailThreshold = 3;

    // Headings are compared with everything but letters and digits removed, in lower case, so
    // "Contact Person", "contact_person" and "*ContactPerson" are one heading. Deliberately NOT
    // included: "ContactName". Xero uses it for the COMPANY and most people mean the PERSON, so either
    // reading would file somebody's list wrongly; it is ignored, and the preview says it was.
    private static readonly Dictionary<string, Field> Headings = new()
    {
        ["client"] = Field.Client, ["clientname"] = Field.Client, ["company"] = Field.Client,
        ["companyname"] = Field.Client, ["customer"] = Field.Client, ["customername"] = Field.Client,
        ["name"] = Field.Client, ["organisation"] = Field.Client, ["organization"] = Field.Client,
        ["business"] = Field.Client, ["businessname"] = Field.Client, ["account"] = Field.Client,
        ["accountname"] = Field.Client,

        ["contact"] = Field.Contact, ["contactperson"] = Field.Contact, ["person"] = Field.Contact,
        ["attention"] = Field.Contact, ["attentionto"] = Field.Contact, ["attn"] = Field.Contact,

        ["phone"] = Field.Phone, ["phonenumber"] = Field.Phone, ["telephone"] = Field.Phone,
        ["telephonenumber"] = Field.Phone, ["tel"] = Field.Phone, ["mobile"] = Field.Phone,
        ["mobilenumber"] = Field.Phone, ["cell"] = Field.Phone, ["cellphone"] = Field.Phone,
        ["cellnumber"] = Field.Phone, ["contactnumber"] = Field.Phone,

        ["email"] = Field.Email, ["emailaddress"] = Field.Email, ["mail"] = Field.Email,

        ["address"] = Field.Address, ["physicaladdress"] = Field.Address, ["streetaddress"] = Field.Address,
        ["addressline1"] = Field.Address, ["addressline2"] = Field.Address, ["addressline3"] = Field.Address,
        ["suburb"] = Field.Address, ["city"] = Field.Address, ["town"] = Field.Address,

        ["notes"] = Field.Notes, ["note"] = Field.Notes, ["comments"] = Field.Notes, ["comment"] = Field.Notes,
    };

    /// <summary>One line of the file, cleaned. <see cref="Line"/> is the row number Excel shows.</summary>
    public sealed class Row
    {
        public int Line { get; set; }
        public string Client { get; set; } = "";
        public string Contact { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Email { get; set; } = "";
        public string Address { get; set; } = "";
        public string Notes { get; set; } = "";
        /// <summary>What was put right on the way in, in words for the preview.</summary>
        public List<string> Fixes { get; set; } = new();
        /// <summary>Why this row cannot go in. Empty when it can.</summary>
        public string Error { get; set; } = "";
        /// <summary>A doubt about the row that does not stop it, such as an email that is not one.</summary>
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class Parsed
    {
        public List<Row> Rows { get; } = new();
        /// <summary>How each heading in the file was read, in file order: "PhoneNumber → Phone".</summary>
        public List<string> ColumnsUsed { get; } = new();
        /// <summary>Headed columns that mean nothing to us, so the person knows they were not missed by accident.</summary>
        public List<string> ColumnsIgnored { get; } = new();
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Reads a sheet into rows. The first row with anything in it is the headings, and one of them
    /// must mean Client. <paramref name="southAfrica"/> decides only the phone repairs — a leading
    /// zero is how every South African number starts, which is not true of every country.
    /// </summary>
    public static Parsed Parse(Spreadsheet.Sheet sheet, bool southAfrica)
    {
        var result = new Parsed();
        int h = sheet.Rows.FindIndex(r => r.Any(c => c.Text.Trim().Length > 0));
        if (h < 0) { result.Error = "The file has nothing in it."; return result; }

        var header = sheet.Rows[h];
        var map = new Dictionary<int, Field>();
        for (int i = 0; i < header.Length; i++)
        {
            var raw = Collapse(RepairMojibake(header[i].Text));
            if (raw.Length == 0) continue;
            if (Headings.TryGetValue(Key(raw), out var f))
            {
                // Two columns meaning Client is ambiguous; the first is the client and the second is
                // ignored. Every other field happily takes several — Phone and Mobile, Address lines.
                if (f == Field.Client && map.ContainsValue(Field.Client)) { result.ColumnsIgnored.Add(raw); continue; }
                map[i] = f;
                result.ColumnsUsed.Add(raw == Label(f) ? raw : $"{raw} → {Label(f)}");
            }
            else result.ColumnsIgnored.Add(raw);
        }
        if (!map.ContainsValue(Field.Client))
        {
            // Every heading the row had, understood or not — "found: none" beside a row that says
            // Contact and Phone would send the person looking for a problem they do not have.
            var found = header.Select(c => Collapse(RepairMojibake(c.Text))).Where(s => s.Length > 0).ToList();
            result.Error = "The first row must be the column headings, and one of them must be Client. " +
                           "Headings found: " + (found.Count > 0 ? string.Join(", ", found) : "none") + ".";
            return result;
        }

        var headerKey = string.Join("|", header.Select(c => Key(c.Text)));
        for (int r = h + 1; r < sheet.Rows.Count; r++)
        {
            var cells = sheet.Rows[r];
            if (!cells.Any(c => c.Text.Trim().Length > 0)) continue;              // a blank row is nothing
            if (string.Join("|", cells.Select(c => Key(c.Text))) == headerKey) continue;   // headings pasted twice
            if (result.Rows.Count >= MaxRows)
            {
                result.Error = $"The file has more than {MaxRows:N0} rows. Split it into smaller files and import each.";
                return result;
            }

            var row = new Row { Line = r + 1 };
            var parts = new Dictionary<Field, List<string>>();
            // Phone and Email hold ONE value each, and only something that can be dialled or mailed —
            // Clint, 2026-09-28: clients will be emailed and texted when a job changes stage, and
            // "082 891 9508 (Butch)" or "a@x; b@y" cannot be sent to. Whatever does not fit goes to
            // Notes word for word, so nothing in the file is lost.
            var numbers = new List<string>();
            var emails = new List<string>();
            var asWritten = new List<string>();
            foreach (var (col, field) in map)
            {
                if (col >= cells.Length) continue;
                var cell = cells[col];
                var text = RepairMojibake(cell.Text, out var repaired);
                if (repaired) row.Fixes.Add($"{Label(field)}: letters repaired (\"{Collapse(cell.Text)}\" → \"{Collapse(text)}\")");

                if (field == Field.Phone)
                {
                    var (number, spare, fixes) = SplitPhone(text, cell.IsNumber, southAfrica);
                    row.Fixes.AddRange(fixes);
                    if (spare) asWritten.Add($"Phone as written: {Collapse(text)}");
                    if (number.Length > 0 && !numbers.Contains(number)) numbers.Add(number);
                    continue;
                }
                if (field == Field.Email)
                {
                    foreach (var e in SplitEmails(text))
                    {
                        if (!LooksLikeEmail(e))
                        {
                            asWritten.Add($"Email as written: {e}");
                            row.Warnings.Add($"\"{e}\" is not an email address, so it went to Notes");
                        }
                        else if (!emails.Contains(e, StringComparer.OrdinalIgnoreCase)) emails.Add(e);
                    }
                    continue;
                }

                var value = field is Field.Address or Field.Notes ? Lines(text) : Collapse(text);
                if (value.Length == 0) continue;
                if (!parts.TryGetValue(field, out var list)) parts[field] = list = new List<string>();
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            }

            string Join(Field f, string sep) => parts.TryGetValue(f, out var l) ? string.Join(sep, l) : "";
            row.Client = Join(Field.Client, " ");
            row.Contact = Join(Field.Contact, " ");
            row.Address = Join(Field.Address, ", ");

            row.Phone = numbers.FirstOrDefault() ?? "";
            foreach (var n in numbers.Skip(1)) asWritten.Add($"Other number: {n}");
            if (numbers.Count > 1) row.Fixes.Add($"Phone: {row.Phone} kept; {numbers.Count - 1} more moved to Notes");
            row.Email = emails.FirstOrDefault() ?? "";
            foreach (var e in emails.Skip(1)) asWritten.Add($"Other email: {e}");
            if (emails.Count > 1) row.Fixes.Add($"Email: {row.Email} kept; {emails.Count - 1} more moved to Notes");
            row.Notes = string.Join("\n", (parts.TryGetValue(Field.Notes, out var own) ? own : new List<string>()).Concat(asWritten));

            if (row.Client.Length == 0) row.Error = "No client name.";
            result.Rows.Add(row);
        }
        if (result.Rows.Count == 0 && result.Error.Length == 0) result.Error = "The file has headings but no clients under them.";
        return result;
    }

    public static string Label(Field f) => f switch
    {
        Field.Client => "Client", Field.Contact => "Contact person", Field.Phone => "Phone",
        Field.Email => "Email", Field.Address => "Address", _ => "Notes",
    };

    /// <summary>The name two rows, or a row and a client on file, are the same client by.</summary>
    public static string NameKey(string name) => Collapse(name).ToLowerInvariant();

    /// <summary>
    /// Every address on at least <see cref="SharedEmailThreshold"/> different clients, with how many.
    /// Counted by CLIENT, so one company's three contacts sharing the office address do not trip it.
    /// </summary>
    public static List<(string email, int clients)> SharedEmails(IEnumerable<Row> rows) =>
        rows.Where(r => r.Error.Length == 0)
            .SelectMany(r => r.Email.Split(';').Select(e => (e: e.Trim().ToLowerInvariant(), c: NameKey(r.Client))))
            .Where(x => x.e.Length > 0)
            .GroupBy(x => x.e)
            .Select(g => (email: g.Key, clients: g.Select(x => x.c).Distinct().Count()))
            .Where(x => x.clients >= SharedEmailThreshold)
            .OrderByDescending(x => x.clients).ThenBy(x => x.email)
            .ToList();

    /// <summary>The row's email with every left-out address taken away.</summary>
    public static string WithoutLeftOut(string email, ISet<string> leaveOut) =>
        leaveOut.Count == 0 || email.Length == 0 ? email
            : string.Join("; ", email.Split(';').Select(e => e.Trim()).Where(e => e.Length > 0 && !leaveOut.Contains(e.ToLowerInvariant())));

    // ---------------------------------------------------------------- repairs

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public static string Collapse(string s) => Space.Replace(s ?? "", " ").Trim();

    /// <summary>Keeps line breaks, which an address or a note may mean, and tidies each line.</summary>
    private static string Lines(string s) =>
        string.Join("\n", (s ?? "").Replace("\r", "").Split('\n').Select(Collapse).Where(l => l.Length > 0));

    private static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s ?? "") if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public static bool LooksLikeEmail(string e) => EmailShape.IsMatch(e);

    public static string RepairMojibake(string s) => RepairMojibake(s, out _);

    /// <summary>
    /// Undoes UTF-8 read as Windows-1252: "Ã©" back to "é", "â€“" back to "–". It happens when an
    /// export is opened in Excel with the wrong encoding and then saved — Clint's list had
    /// "Dr AndrÃ© Wiid" and "Ã‰mile" in it.
    ///
    /// Safe because it is all or nothing: the string is turned back into the bytes Windows-1252 would
    /// give, and only if those bytes are VALID UTF-8 is the result used. Correctly written text almost
    /// never passes that test — "André" gives a lone 0xE9, which is not UTF-8 — so it is left alone.
    /// </summary>
    public static string RepairMojibake(string s, out bool repaired)
    {
        repaired = false;
        if (string.IsNullOrEmpty(s) || !s.Any(c => c is >= 'Â' and <= 'ô')) return s ?? "";
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var b = Spreadsheet.To1252(s[i]);
            if (b == null) return s;
            bytes[i] = b.Value;
        }
        try
        {
            var fixedText = new UTF8Encoding(false, true).GetString(bytes);
            repaired = fixedText != s;
            return fixedText;
        }
        catch (DecoderFallbackException) { return s; }
    }

    // A run of what a phone number is written with, starting and ending on a digit.
    private static readonly Regex NumberRun = new(@"\+?\(?\d[\d\s().\-]*\d", RegexOptions.Compiled);
    private static readonly Regex EmailSeparators = new(@"[;,/]+", RegexOptions.Compiled);

    /// <summary>
    /// ONE number out of a phone cell, as it would be dialled, and whether anything else was in the
    /// cell — "082 891 9508 (Butch)", "Sadiya: 083 601 3000", "065 526 5961 WHATSAPP". The caller
    /// keeps the whole cell in Notes when there was, so the name or the WhatsApp is not lost.
    ///
    /// The number is taken out only when the words around it have no digits of their own. In
    /// "1 USA 407 252 7159" the 1 is the country code, and taking 407 252 7159 alone would dial the
    /// wrong country — so a cell like that is kept whole, exactly as written.
    /// </summary>
    public static (string number, bool spare, List<string> fixes) SplitPhone(string raw, bool isNumber, bool southAfrica)
    {
        var fixes = new List<string>();
        var (whole, fix) = CleanPhone(raw, isNumber, southAfrica);
        if (fix != null) fixes.Add(fix);
        if (whole.Length == 0) return ("", false, fixes);

        var run = NumberRun.Matches(whole).FirstOrDefault(m => m.Value.Count(char.IsDigit) >= 7);
        if (run == null)
        {
            fixes.Add($"Phone: \"{whole}\" is not a number, moved to Notes");
            return ("", true, fixes);
        }
        var around = (whole[..run.Index] + " " + whole[(run.Index + run.Length)..]).Trim(' ', '-', ':', ',', ';', '/', '(', ')', '.');
        if (around.Length == 0) return (whole, false, fixes);
        if (around.Any(char.IsDigit)) return (whole, false, fixes);

        var (number, again) = CleanPhone(run.Value, false, southAfrica);
        if (again != null) fixes.Add(again);
        fixes.Add($"Phone: \"{around}\" moved to Notes (\"{whole}\" → {number})");
        return (number, true, fixes);
    }

    /// <summary>
    /// Every address in an email cell — "a@x; b@y", "a@x, b@y", "a@x / b@y", "a@x b@y" — each on its
    /// own. Split on spaces only where there are two @ signs, so "bob at acme" stays one (wrong)
    /// entry for Notes rather than three.
    /// </summary>
    public static List<string> SplitEmails(string raw) =>
        EmailSeparators.Split(Collapse(raw).Replace("mailto:", " ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(piece => piece.Count(ch => ch == '@') > 1 ? piece.Split(' ') : new[] { piece })
            .Select(e => e.Trim(' ', '<', '>', '(', ')', '"', '\'', '.'))
            .Where(e => e.Length > 0).ToList();

    // "RSA 27 082 568 7976", "UK 44 7781 136389", "CA 1 (416) 574-5543" — how Xero writes a number:
    // country letters, calling code, then the rest.
    // A calling code never starts with 0, which is what keeps "RSA 082 123 4567" out of this one.
    private static readonly Regex CountryAndCode = new(@"^(?:[A-Za-z]{2,3}\s+)+([1-9]\d{0,2})\s+(?:\(0\)\s*)?(\(?\d.*)$", RegexOptions.Compiled);
    private static readonly Regex CountryThenLocal = new(@"^(?:[A-Za-z]{2,3}\s+)+(0\d.*)$", RegexOptions.Compiled);
    private static readonly Regex CountryOnly = new(@"^[A-Za-z]{2,3}$", RegexOptions.Compiled);
    // +27 82 123 4567, 27 82 123 4567, +27 (0)82… — South Africa's code with no letters in front.
    private static readonly Regex SouthAfricanCode = new(@"^\+?27\s*(?:\(0\)\s*)?([\d\s]+)$", RegexOptions.Compiled);
    private static readonly Regex DigitsAndSpaces = new(@"^[\d\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// A phone number as it would be dialled. Words after a number, and anything not recognised, are
    /// kept exactly as written.
    ///
    /// Everywhere: Xero's "UK 44 7781 136389" becomes +44 7781 136389, which dials from any phone,
    /// and a phone that is only country letters ("RSA", no number) is left blank.
    ///
    /// In South Africa, where every local number is 0 and nine digits, these are certain, and the
    /// number becomes what a customer reads out and what the client search is typed as:
    /// - nine digits with no leading 0 have lost it — Excel does it to a number cell, and people do
    ///   it typing "82 691 0211" (0826910211);
    /// - 27, +27 or "RSA 27" before nine digits is the international form (+27 82… → 082…).
    /// The digit count is the safeguard: "27 11 477 2140" is Johannesburg, "083 380 377" is a number
    /// with a digit missing, and only the first is changed.
    /// </summary>
    public static (string phone, string? fix) CleanPhone(string raw, bool isNumber, bool southAfrica)
    {
        var p = Collapse(raw);
        if (p.Length == 0) return (p, null);
        if (CountryOnly.IsMatch(p)) return ("", $"Phone: \"{p}\" is not a number, left blank");

        if (southAfrica)
        {
            int digits = p.Count(char.IsDigit);
            if (DigitsAndSpaces.IsMatch(p) && digits == 9 && p[0] != '0')
                return ("0" + p, $"Phone: leading 0 put back ({p} → 0{p})");

            var za = SouthAfricanCode.Match(p);
            if (za.Success && za.Groups[1].Value.Count(char.IsDigit) == 9 && !za.Groups[1].Value.TrimStart().StartsWith('0'))
            {
                var local = "0" + za.Groups[1].Value.Trim();
                return (local, $"Phone: written as a local number ({p} → {local})");
            }
            if (za.Success && za.Groups[1].Value.Count(char.IsDigit) == 10 && za.Groups[1].Value.TrimStart().StartsWith('0'))
            {
                var local = za.Groups[1].Value.Trim();
                return (local, $"Phone: written as a local number ({p} → {local})");
            }
            if (isNumber) return (p, null);
        }

        // Only when what follows the code is long enough to be a whole number: "RSA 382 1330" is a
        // Knysna number with no code at all, and reading 382 as one made it Montenegro.
        var m = CountryAndCode.Match(p);
        var restDigits = m.Success ? m.Groups[2].Value.Count(char.IsDigit) : 0;
        if (m.Success && restDigits is >= 6 and <= 12)
        {
            var code = m.Groups[1].Value;
            var rest = m.Groups[2].Value.Trim();
            string? result = null;
            if (southAfrica && code == "27")
            {
                var local = rest.StartsWith('0') ? rest : "0" + rest;
                if (local.Count(char.IsDigit) == 10) result = local;
            }
            else result = "+" + code + " " + (rest.StartsWith('0') && !rest.StartsWith("0 ") ? rest[1..] : rest);
            if (result != null)
                return (result, $"Phone: written as {(result.StartsWith('+') ? "an international" : "a local")} number ({p} → {result})");
        }
        if (southAfrica)
        {
            var l = CountryThenLocal.Match(p);
            if (l.Success) return (l.Groups[1].Value.Trim(), $"Phone: country letters removed ({p} → {l.Groups[1].Value.Trim()})");
        }
        return (p, null);
    }
}

/// <summary>
/// The half of the import that reads and writes: what the rows would do to the clients on file, and
/// then doing it. See <see cref="ClientImport"/> for the rules.
/// </summary>
public sealed class ClientImportService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public ClientImportService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    public enum Outcome { New, AddsToExisting, NothingNew, Error }

    /// <summary>What importing does to one client — every row that named it, taken together.</summary>
    public sealed class ClientPlan
    {
        public string Name { get; set; } = "";
        public Outcome Outcome { get; set; }
        public List<int> Lines { get; } = new();
        public List<string> NewContacts { get; } = new();
        /// <summary>For a client already on file: the fields that were blank and will be filled.</summary>
        public List<string> Fills { get; } = new();
        public List<string> Fixes { get; } = new();
        public List<string> Warnings { get; } = new();
        public string Error { get; set; } = "";
        public bool Archived { get; set; }
        // What the client will say once imported, for the preview to show beside its name.
        public string Contact { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Email { get; set; } = "";
        public string Address { get; set; } = "";
    }

    public sealed class Plan
    {
        public List<ClientPlan> Clients { get; } = new();
        public int New => Clients.Count(c => c.Outcome == Outcome.New);
        public int AddsToExisting => Clients.Count(c => c.Outcome == Outcome.AddsToExisting);
        public int NothingNew => Clients.Count(c => c.Outcome == Outcome.NothingNew);
        public int Errors => Clients.Count(c => c.Outcome == Outcome.Error);
        public int ContactsAdded => Clients.Sum(c => c.NewContacts.Count);
    }

    public Task<Plan> PlanAsync(int tenantId, IReadOnlyList<ClientImport.Row> rows, ISet<string> leaveOut) =>
        RunAsync(tenantId, rows, leaveOut, save: false);

    /// <summary>
    /// Imports, if this person may. Asked here and not only on the page (§34): a refused page is not
    /// the same as a refused action, and this is the action.
    /// </summary>
    public async Task<(Plan? plan, string error)> ImportAsync(int tenantId, TenantRole role,
        IReadOnlyList<ClientImport.Row> rows, ISet<string> leaveOut)
    {
        if (!Permissions.CanImportClients(role))
            return (null, "Only the owner, a supervisor or the office administrator can import clients.");
        return (await RunAsync(tenantId, rows, leaveOut, save: true), "");
    }

    /// <summary>
    /// Planning and saving are the same walk, so the preview cannot describe something the save does
    /// not do. Everything is loaded once and saved once: a thousand clients is one transaction, and a
    /// failure half way leaves nothing half imported.
    /// </summary>
    private async Task<Plan> RunAsync(int tenantId, IReadOnlyList<ClientImport.Row> rows, ISet<string> leaveOut, bool save)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var clients = await db.Clients.Where(c => c.TenantId == tenantId).ToListAsync();
        var contacts = await db.Contacts.Where(c => c.TenantId == tenantId).ToListAsync();

        // By name, the way ClientService.ByNameAsync matches — archived included, so an import cannot
        // create a second Acme beside the archived one.
        var byName = new Dictionary<string, Client>();
        foreach (var c in clients.OrderBy(c => c.Archived).ThenBy(c => c.Id)) byName.TryAdd(ClientImport.NameKey(c.Name), c);
        var peopleOf = contacts.GroupBy(c => c.ClientId).ToDictionary(g => g.Key, g => g.ToList());
        var newPeople = new Dictionary<Client, List<Contact>>();

        var plan = new Plan();
        var plans = new Dictionary<string, ClientPlan>();

        foreach (var row in rows)
        {
            if (row.Error.Length > 0)
            {
                plan.Clients.Add(new ClientPlan
                {
                    Name = row.Client.Length > 0 ? row.Client : $"(row {row.Line})",
                    Outcome = Outcome.Error, Error = row.Error, Lines = { row.Line },
                });
                continue;
            }

            var key = ClientImport.NameKey(row.Client);
            var email = ClientImport.WithoutLeftOut(row.Email, leaveOut);
            if (!plans.TryGetValue(key, out var cp))
            {
                cp = new ClientPlan { Name = ClientImport.Collapse(row.Client) };
                plans[key] = cp;
                plan.Clients.Add(cp);
                if (byName.TryGetValue(key, out var existing))
                {
                    cp.Outcome = Outcome.NothingNew;
                    cp.Name = existing.Name;
                    cp.Archived = existing.Archived;
                }
                else
                {
                    cp.Outcome = Outcome.New;
                    var c = new Client { TenantId = tenantId, Name = cp.Name };
                    byName[key] = c;
                    if (save) db.Clients.Add(c);
                }
            }
            cp.Lines.Add(row.Line);
            cp.Fixes.AddRange(row.Fixes.Select(f => $"row {row.Line}: {f}"));
            cp.Warnings.AddRange(row.Warnings.Select(w => $"row {row.Line}: {w}"));

            var client = byName[key];
            bool isNew = cp.Outcome == Outcome.New;

            // The client's own fields: blank ones only. For a new client that is all of them, from its
            // first row; for one on file it is exactly the "fills in what is missing" promise.
            Fill(client.ContactName, row.Contact, v => client.ContactName = v, "contact person");
            Fill(client.Phone, row.Phone, v => client.Phone = v, "phone");
            Fill(client.Email, email, v => client.Email = v, "email");
            Fill(client.Address, row.Address, v => client.Address = v, "address");
            Fill(client.Notes, row.Notes, v => client.Notes = v, "notes");

            if (row.Contact.Length > 0)
            {
                var onFile = client.Id > 0 && peopleOf.TryGetValue(client.Id, out var l) ? l : new List<Contact>();
                if (!newPeople.TryGetValue(client, out var added)) newPeople[client] = added = new List<Contact>();
                var person = onFile.Concat(added).FirstOrDefault(p => ClientImport.NameKey(p.Name) == ClientImport.NameKey(row.Contact));
                if (person == null)
                {
                    person = new Contact { TenantId = tenantId, Client = client, Name = row.Contact, Phone = row.Phone, Email = email };
                    added.Add(person);
                    cp.NewContacts.Add(row.Contact);
                    if (save) db.Contacts.Add(person);
                    if (!isNew) cp.Outcome = Outcome.AddsToExisting;
                }
                else
                {
                    var who = person.Name;
                    FillPerson(person.Phone, row.Phone, v => person.Phone = v, $"{who}'s phone");
                    FillPerson(person.Email, email, v => person.Email = v, $"{who}'s email");
                }
            }

            void Fill(string current, string incoming, Action<string> set, string what)
            {
                if (current.Length > 0 || incoming.Length == 0) return;
                set(incoming);
                if (!isNew) { cp.Fills.Add(what); cp.Outcome = Outcome.AddsToExisting; }
            }
            void FillPerson(string current, string incoming, Action<string> set, string what)
            {
                if (current.Length > 0 || incoming.Length == 0) return;
                set(incoming);
                // A contact only just added by an earlier row is part of "new"; one on file is a fill.
                if (!isNew && !cp.NewContacts.Contains(row.Contact)) { cp.Fills.Add(what); cp.Outcome = Outcome.AddsToExisting; }
            }
        }

        foreach (var (key, cp) in plans)
        {
            var c = byName[key];
            cp.Contact = c.ContactName; cp.Phone = c.Phone; cp.Email = c.Email; cp.Address = c.Address;
        }

        if (save) await db.SaveChangesAsync();
        return plan;
    }
}
