using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Import a client list from another program: download the template or bring your own, upload,
/// see what will happen, then import. The owner, a supervisor or the office administrator —
/// <see cref="Permissions.CanImportClients"/>, asked again by the service that does the saving.
///
/// The preview carries the parsed rows to the Import button inside the form, sealed with Data
/// Protection so the posted copy is the one that was previewed and belongs to this company. Nothing is
/// held on the server between the two steps, so a restart in between costs a re-upload, not a
/// half-finished import.
/// </summary>
public sealed class ClientImportModel : PortalPage
{
    private readonly ClientImportService _import;
    private readonly ITimeLimitedDataProtector _seal;

    public ClientImportModel(TenantService t, ClientImportService import, IDataProtectionProvider dp) : base(t)
    {
        _import = import;
        _seal = dp.CreateProtector("RemoteSupport.ClientImport.v1").ToTimeLimitedDataProtector();
    }

    public bool CanImport => Permissions.CanImportClients(MyRole);

    public string FileName { get; private set; } = "";
    public ClientImport.Parsed? Parsed { get; private set; }
    public ClientImportService.Plan? Plan { get; private set; }
    public List<(string email, int clients)> Shared { get; private set; } = new();
    public string Payload { get; private set; } = "";
    public ClientImportService.Plan? Done { get; private set; }

    private sealed record Sealed(int TenantId, string FileName, List<ClientImport.Row> Rows, List<string> Used, List<string> Ignored);

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Redirect("/login");

    public async Task<IActionResult> OnGetTemplateAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        var readMe = new[]
        {
            "How to fill in the client import template",
            "",
            "Put one person per row on the Clients sheet. Leave the first row, the headings, as it is.",
            "",
            "Client — required. The company, or the customer's own name if they are a private person.",
            "Contact person — who you deal with there. Several rows with the same Client become one client with several contact people.",
            "Phone — as you would dial it, e.g. 082 123 4567. The column is set to text so Excel keeps the leading 0.",
            "Email, Address, Notes — optional.",
            "",
            "Save it as an Excel workbook (.xlsx) or a CSV, then upload it on Clients → Import clients.",
            "Nothing is saved until you have seen a preview. A client already on file is never overwritten: only its blank details are filled in.",
            "Only the Clients sheet is read. This sheet can stay or go.",
        };
        var bytes = Spreadsheet.WriteTemplate(ClientImport.TemplateHeaders,
            new double[] { 40, 26, 18, 32, 40, 40 }, new HashSet<int> { 2 }, readMe);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Client import template.xlsx");
    }

    public async Task<IActionResult> OnPostPreviewAsync(IFormFile? file)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!CanImport) return Page();
        if (file == null || file.Length == 0) { Error = "Choose a file to import first."; return Page(); }
        if (file.Length > Spreadsheet.MaxBytes) { Error = "That file is larger than 10 MB, which is far bigger than any client list."; return Page(); }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var (sheet, err) = Spreadsheet.Read(ms.ToArray(), file.FileName);
        if (sheet == null) { Error = err; return Page(); }

        FileName = Path.GetFileName(file.FileName);
        Parsed = ClientImport.Parse(sheet, Tenant.IsSouthAfrica);
        if (Parsed.Error.Length > 0) { Error = Parsed.Error; return Page(); }

        Shared = ClientImport.SharedEmails(Parsed.Rows);
        Plan = await _import.PlanAsync(TenantId, Parsed.Rows, Shared.Select(s => s.email).ToHashSet());
        Payload = _seal.Protect(JsonSerializer.Serialize(new Sealed(TenantId, FileName, Parsed.Rows, Parsed.ColumnsUsed, Parsed.ColumnsIgnored)),
            TimeSpan.FromHours(2));
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(string payload, string[]? leaveOut)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!CanImport) return Page();

        Sealed? s;
        try { s = JsonSerializer.Deserialize<Sealed>(_seal.Unprotect(payload ?? "")); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        { s = null; }
        if (s == null || s.TenantId != TenantId)
        {
            Error = "That preview has expired. Upload the file again — nothing was imported.";
            return Page();
        }

        var (plan, err) = await _import.ImportAsync(TenantId, MyRole, s.Rows,
            (leaveOut ?? Array.Empty<string>()).Select(e => e.Trim().ToLowerInvariant()).ToHashSet());
        if (plan == null) { Error = err; return Page(); }

        Done = plan;
        FileName = s.FileName;
        Message = $"Imported from {s.FileName}: {plan.New} new client{(plan.New == 1 ? "" : "s")}, " +
                  $"{plan.AddsToExisting} already on file with details added, {plan.ContactsAdded} contact {(plan.ContactsAdded == 1 ? "person" : "people")}.";
        return Page();
    }
}
