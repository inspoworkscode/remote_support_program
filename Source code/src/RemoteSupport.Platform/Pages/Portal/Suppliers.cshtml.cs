using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The company's suppliers, and what every quote request says about delivery (Clint, 2026-09-29:
/// "We need to be able to add suppliers much like the adding items, so that when doing a quote
/// request, the office administrator can select multiple suppliers at once" — and "a supplier setup
/// option in the portal ... on ours to would be accounts@anywhereit.co.za. There should also be an
/// option with delivery address"). Anybody may read it; the people who request quotes change it.
/// </summary>
public sealed class SuppliersModel : PortalPage
{
    private readonly QuoteService _quotes;
    private readonly QuoteSignatureService _sigs;
    public SuppliersModel(TenantService t, QuoteService quotes, QuoteSignatureService sigs) : base(t) { _quotes = quotes; _sigs = sigs; }

    public List<Supplier> Suppliers { get; private set; } = new();
    public bool CanEdit => Permissions.CanEditSuppliers(MyRole);
    /// <summary>Set after "did you mean": what was typed, and the look-alikes already on the list.</summary>
    public List<Supplier> Similar { get; private set; } = new();
    public Supplier Pending { get; private set; } = new();

    // ---- signatures (2026-10-06) ----
    public List<QuoteSignature> Signatures { get; private set; } = new();
    /// <summary>The signature open in the editor: a new one (Id 0), one being changed, or null for none open.</summary>
    [BindProperty(SupportsGet = true)] public int? Edit { get; set; }
    public QuoteSignature? Editing { get; private set; }
    /// <summary>The lines drawn in the editor — what was posted when a save was refused, so nothing typed is lost.</summary>
    public List<QuoteSignatures.Line> EditLines { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? await Render() : Redirect("/login");

    private async Task<IActionResult> Render()
    {
        Suppliers = await _quotes.SuppliersAsync(TenantId);
        Signatures = await _sigs.ListAsync(TenantId);
        if (CanEdit && Edit is { } e && Editing == null)
        {
            Editing = e == 0 ? new QuoteSignature() : Signatures.FirstOrDefault(s => s.Id == e);
            if (Editing != null && EditLines.Count == 0) EditLines = QuoteSignatures.LinesOf(Editing);
        }
        if (Editing != null && EditLines.Count == 0)
            EditLines.Add(new QuoteSignatures.Line("", QuoteSignatures.DefaultFont, QuoteSignatures.DefaultSize, QuoteSignatures.DefaultColor, "normal"));
        return Page();
    }

    /// <summary>Adds or changes a signature. The lines come as parallel lists, one entry per row of the editor.</summary>
    public async Task<IActionResult> OnPostSignatureAsync(int signatureId, string? name, List<string>? lineText, List<string>? lineFont,
        List<int>? lineSize, List<string>? lineColor, List<string>? lineStyle, IFormFile? image, bool removeImage, int imageWidth, string? imagePlace)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var texts = lineText ?? new();
        var lines = texts.Select((t, i) => new QuoteSignatures.Line(t ?? "",
            lineFont?.ElementAtOrDefault(i) ?? "", lineSize?.ElementAtOrDefault(i) ?? 0,
            lineColor?.ElementAtOrDefault(i) ?? "", lineStyle?.ElementAtOrDefault(i) ?? "")).ToList();
        byte[]? bytes = null;
        if (image is { Length: > 0 })
        {
            if (image.Length > QuoteSignatures.MaxImageBytes) { Error = "That image is larger than 1 MB. Save it smaller and try again."; return await Refused(signatureId, name, lines, imagePlace); }
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        var (sig, err) = await _sigs.SaveAsync(TenantId, signatureId, name, lines, bytes, removeImage, imageWidth, imagePlace == "above", Me, MyRole);
        if (sig == null) { Error = err; return await Refused(signatureId, name, lines, imagePlace); }
        Message = signatureId == 0 ? $"Signature \"{sig.Name}\" added." : $"Signature \"{sig.Name}\" saved.";
        Edit = null;
        return await Render();
    }

    /// <summary>A refused save comes back open, with every line as it was typed.</summary>
    private async Task<IActionResult> Refused(int signatureId, string? name, List<QuoteSignatures.Line> lines, string? imagePlace)
    {
        var kept = signatureId == 0 ? null : await _sigs.GetAsync(TenantId, signatureId);
        Editing = new QuoteSignature
        {
            Id = signatureId, Name = name ?? "", ImageFirst = imagePlace == "above",
            Image = kept?.Image, ImageType = kept?.ImageType ?? "", ImageToken = kept?.ImageToken ?? "", ImageWidth = kept?.ImageWidth ?? 0
        };
        Edit = signatureId;
        EditLines = lines.Select(l => QuoteSignatures.Clean(l.Text, l.Font, l.Size, l.Color, l.Style)).ToList();
        return await Render();
    }

    public async Task<IActionResult> OnPostRemoveSignatureAsync(int signatureId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _sigs.RemoveAsync(TenantId, signatureId, MyRole);
        if (err.Length > 0) Error = err; else Message = "Signature removed. Requests already signed with it are downloaded again the same.";
        return await Render();
    }

    public async Task<IActionResult> OnPostSettingsAsync(string? quoteTo, string? delivery)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _quotes.SaveQuoteSettingsAsync(TenantId, quoteTo, delivery, MyRole);
        if (err.Length > 0) Error = err;
        else { Message = "Saved. Every quote request from now on uses these."; await LoadAsync(); }
        return await Render();
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, string? email, string? contactPerson, string? phone, bool confirmNew)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (s, err, similar) = await _quotes.AddSupplierAsync(TenantId, name, email, contactPerson, phone, Me, MyRole, confirmNew);
        if (s != null) Message = $"{s.Name} added.";
        else
        {
            Similar = similar;
            if (similar.Count == 0) Error = err;
            Pending = new Supplier { Name = name ?? "", Email = email ?? "", ContactPerson = contactPerson ?? "", Phone = phone ?? "" };
        }
        return await Render();
    }

    public async Task<IActionResult> OnPostSaveAsync(int supplierId, string? name, string? email, string? contactPerson, string? phone)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _quotes.UpdateSupplierAsync(TenantId, supplierId, name, email, contactPerson, phone, MyRole);
        if (err.Length > 0) Error = err; else Message = "Saved.";
        return await Render();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int supplierId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _quotes.RemoveSupplierAsync(TenantId, supplierId, MyRole);
        if (err.Length > 0) Error = err; else Message = "Removed from the list. Tickets that asked them still say so.";
        return await Render();
    }
}
