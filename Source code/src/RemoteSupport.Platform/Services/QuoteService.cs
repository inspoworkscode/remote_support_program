using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The company's list of quotable items, and the lines of a workshop quote (stage 3.7, step 3).
///
/// <para>The item list is shared by the whole company and grows as people quote (answer 6). The
/// problem Clint named is duplicates: "one technician might start with DDR for memory and another
/// might say memory DDR. it must be able to recognise each to prevent duplicates being added."
/// Two tools answer it. <see cref="SuggestAsync"/> is the predictive text — every word typed must
/// START a word of the item, in any order, so "mem ddr" finds "DDR4 memory 8GB". And
/// <see cref="AddItemAsync"/> refuses a name whose words are the same words as an existing item's
/// (<see cref="MatchKey"/>), and asks "did you mean" when the words merely overlap.</para>
/// </summary>
public sealed class QuoteService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public QuoteService(IDbContextFactory<PlatformDb> dbf) => _dbf = dbf;

    // ---------------------------------------------------------------- matching

    /// <summary>A name's words, lower-cased, split on anything that is not a letter or a digit.</summary>
    public static string[] Words(string? name) =>
        new string((name ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The same words in alphabetical order: "memory DDR" and "DDR memory" are both "ddr memory".
    /// Two items with the same key are one item written twice.
    /// </summary>
    public static string MatchKey(string? name) => string.Join(" ", Words(name).Distinct().OrderBy(w => w, StringComparer.Ordinal));

    /// <summary>Every typed word starts some word of the item. Order does not matter.</summary>
    public static bool Predicts(string[] typed, string[] item) =>
        typed.Length > 0 && typed.All(t => item.Any(w => w.StartsWith(t, StringComparison.Ordinal)));

    /// <summary>
    /// Close enough to ask "did you mean": either name's words all start words of the other, or
    /// they share at least half their words. Deliberately generous — the question costs one click,
    /// and a duplicate costs a list nobody trusts.
    /// </summary>
    public static bool Resembles(string a, string b)
    {
        var wa = Words(a).Distinct().ToArray();
        var wb = Words(b).Distinct().ToArray();
        if (wa.Length == 0 || wb.Length == 0) return false;
        if (Predicts(wa, wb) || Predicts(wb, wa)) return true;
        int shared = wa.Intersect(wb).Count();
        return shared * 2 >= Math.Max(wa.Length, wb.Length);
    }

    // ---------------------------------------------------------------- the item list

    public async Task<List<CatalogItem>> ItemsAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CatalogItems.Where(i => i.TenantId == tenantId && i.MergedIntoId == null)
            .OrderBy(i => i.Name).ToListAsync();
    }

    /// <summary>The predictive text under the Add item box. Best first: most words matched, then shortest name.</summary>
    public async Task<List<CatalogItem>> SuggestAsync(int tenantId, string typed, int take = 8)
    {
        var words = Words(typed);
        if (words.Length == 0) return new();
        var all = await ItemsAsync(tenantId);
        return all.Where(i => Predicts(words, Words(i.Name)))
            .OrderBy(i => Words(i.Name).Length).ThenBy(i => i.Name)
            .Take(take).ToList();
    }

    /// <summary>
    /// The Item box's dropdown (Clint, 2026-09-29: "a drop down menu where one can select the item
    /// to add if it has already been added before"). Nothing typed is the WHOLE list, by name, so an
    /// item can be picked without guessing how somebody spelt it; something typed narrows it the
    /// way the predictive text always has.
    /// </summary>
    public async Task<List<CatalogItem>> BrowseAsync(int tenantId, string? typed)
    {
        if (Words(typed).Length == 0) return (await ItemsAsync(tenantId)).Take(BrowseLimit).ToList();
        return await SuggestAsync(tenantId, typed!, BrowseLimit);
    }

    /// <summary>More than anybody scrolls through; the typing narrows it long before this matters.</summary>
    public const int BrowseLimit = 500;

    /// <summary>
    /// Adds an item to the company's list — or does not, and says which existing one to use.
    ///
    /// <list type="bullet">
    /// <item>Same words as an existing item: never added. That item is returned, so the form can
    /// simply use it. <paramref name="confirmed"/> does not override this; there is nothing to confirm.</item>
    /// <item>Resembles one or more: not added unless <paramref name="confirmed"/>, and the look-alikes
    /// come back for a "did you mean". "DDR4 8GB" beside "DDR4 16GB" is genuinely a different part.</item>
    /// </list>
    /// <paramref name="partNumber"/> (the Items page's Add an item) becomes the new item's
    /// <see cref="CatalogItem.LastPartNumber"/>; an item already on the list keeps its own.
    /// </summary>
    public async Task<(CatalogItem? item, string error, List<CatalogItem> similar)> AddItemAsync(
        int tenantId, string name, string by, bool confirmed = false, string? partNumber = null)
    {
        name = string.Join(" ", (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var key = MatchKey(name);
        if (key.Length == 0) return (null, "Name the item.", new());
        await using var db = await _dbf.CreateDbContextAsync();
        var live = await db.CatalogItems.Where(i => i.TenantId == tenantId && i.MergedIntoId == null).ToListAsync();

        var same = live.FirstOrDefault(i => i.MatchKey == key);
        if (same != null) return (same, "", new());

        var similar = live.Where(i => Resembles(i.Name, name)).OrderBy(i => i.Name).Take(5).ToList();
        if (similar.Count > 0 && !confirmed)
            return (null, "That looks like an item already on the list. Use one of these, or add it anyway if it really is different.", similar);

        var item = new CatalogItem { TenantId = tenantId, Name = name, MatchKey = key, CreatedBy = by, LastPartNumber = (partNumber ?? "").Trim() };
        db.CatalogItems.Add(item);
        await db.SaveChangesAsync();
        return (item, "", new());
    }

    /// <summary>Renames an item. Refused if the new name is another item's words — that is a merge.</summary>
    public async Task<string> RenameItemAsync(int tenantId, int itemId, string name, TenantRole role)
    {
        if (!Permissions.CanEditItems(role)) return "Only a supervisor, the owner or the office administrator can edit the item list.";
        var key = MatchKey(name);
        if (key.Length == 0) return "Name the item.";
        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == itemId && i.TenantId == tenantId && i.MergedIntoId == null);
        if (item == null) return "That item is not on the list.";
        var clash = await db.CatalogItems.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.MergedIntoId == null && i.Id != itemId && i.MatchKey == key);
        if (clash != null) return $"\"{clash.Name}\" already has those words. Merge the two instead.";
        item.Name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        item.MatchKey = key;
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// Folds one item into another: it leaves the dropdown, and every quote line that pointed at it
    /// points at the survivor. The lines keep the NAME they were written with.
    /// </summary>
    public async Task<string> MergeItemAsync(int tenantId, int fromId, int intoId, TenantRole role)
    {
        if (!Permissions.CanEditItems(role)) return "Only a supervisor, the owner or the office administrator can merge items.";
        if (fromId == intoId) return "Choose two different items.";
        await using var db = await _dbf.CreateDbContextAsync();
        var from = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == fromId && i.TenantId == tenantId && i.MergedIntoId == null);
        var into = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == intoId && i.TenantId == tenantId && i.MergedIntoId == null);
        if (from == null || into == null) return "That item is not on the list.";
        from.MergedIntoId = into.Id;
        if (into.LastPartNumber.Length == 0) into.LastPartNumber = from.LastPartNumber;
        foreach (var line in await db.QuoteLines.Where(l => l.TenantId == tenantId && l.ItemId == fromId).ToListAsync())
            line.ItemId = into.Id;
        await db.SaveChangesAsync();
        return "";
    }

    // ---------------------------------------------------------------- quote lines

    /// <summary>
    /// Where a quote's lines may still be changed: on the bench while the technician is deciding,
    /// and at Quote needed — which is also where a re-quote lands (answer: "items kept and editable").
    /// Once the quote has been requested, the lines are what the supplier was asked for.
    /// </summary>
    public static readonly string[] EditableAt = { Workflow.RepairableStage, Workflow.WorkDoneStage, Workflow.GeneralFirst, "quote-needed" };

    public static bool MayEditLines(Ticket t, TenantRole role, string me) =>
        EditableAt.Contains(t.Stage)
        && (Workflow.MayAct(Workflow.Stage(t.Stage)!.Lane, t, role, me)
            || (Permissions.CanAddWorkDone(role) && me.Length > 0 && Assignees.Has(t.WorkedBy, me)));

    /// <summary>A ticket's quote lines, each with its photos (<see cref="QuoteLine.PhotoIds"/>).</summary>
    public async Task<List<QuoteLine>> LinesAsync(int tenantId, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lines = await db.QuoteLines.Where(l => l.TenantId == tenantId && l.TicketId == ticketId).OrderBy(l => l.Id).ToListAsync();
        if (lines.Count == 0) return lines;
        var ids = lines.Select(l => l.Id).ToList();
        var photos = await db.TicketPhotos
            .Where(p => p.TenantId == tenantId && p.TicketId == ticketId && p.QuoteLineId != null && ids.Contains(p.QuoteLineId!.Value))
            .OrderBy(p => p.Id).Select(p => new { p.Id, p.Token, Line = p.QuoteLineId!.Value }).ToListAsync();
        foreach (var l in lines)
        {
            l.PhotoIds = photos.Where(p => p.Line == l.Id).Select(p => p.Id).ToList();
            l.PhotoTokens = photos.Where(p => p.Line == l.Id).Select(p => p.Token).ToList();
        }
        return lines;
    }

    /// <summary>The most photos one line may have — far more than a part needs, few enough that one
    /// quote request email stays a size a supplier's mail server takes.</summary>
    public const int MaxPhotosPerLine = 12;

    /// <summary>
    /// The photos a line may be given: each one a Part photo already uploaded to THIS ticket and not
    /// yet on a line. Empty error when they all may; the photos themselves, tracked by <paramref name="db"/>.
    /// </summary>
    private static async Task<(List<TicketPhoto> photos, string error)> ClaimPhotosAsync(PlatformDb db, int tenantId, int ticketId,
        IEnumerable<long>? photoIds, int already)
    {
        var ids = (photoIds ?? Array.Empty<long>()).Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return (new(), "");
        if (already + ids.Count > MaxPhotosPerLine) return (new(), $"An item can have up to {MaxPhotosPerLine} photos.");
        var photos = await db.TicketPhotos.Where(p => ids.Contains(p.Id) && p.TenantId == tenantId).ToListAsync();
        if (photos.Count != ids.Count || photos.Any(p => p.TicketId != ticketId || p.Kind != PhotoKind.Part))
            return (new(), ids.Count == 1 ? "That photo is not on this ticket." : "One of those photos is not on this ticket.");
        if (photos.Any(p => p.QuoteLineId != null)) return (new(), "That photo is already on another item.");
        return (photos, "");
    }

    public async Task<(QuoteLine? line, string error)> AddLineAsync(int tenantId, int ticketId, int itemId, string? partNumber,
        int quantity, IEnumerable<long>? photoIds, string by, TenantRole role, string? serialNumber = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return (null, "That ticket is not on file.");
        if (!MayEditLines(t, role, by)) return (null, "The quote's items can be changed on the bench, or at Quote needed.");
        var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == itemId && i.TenantId == tenantId);
        // A merged item is quoted as the one it was merged into.
        if (item?.MergedIntoId != null) item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == item.MergedIntoId && i.TenantId == tenantId);
        if (item == null) return (null, "Choose an item from the list, or add one.");
        var (photos, perr) = await ClaimPhotosAsync(db, tenantId, ticketId, photoIds, 0);
        if (perr.Length > 0) return (null, perr);

        var part = (partNumber ?? "").Trim();
        if (part.Length == 0) part = item.LastPartNumber;   // pre-filled from the item's last use
        var line = new QuoteLine
        {
            TenantId = tenantId, TicketId = ticketId, ItemId = item.Id, ItemName = item.Name,
            PartNumber = part, SerialNumber = (serialNumber ?? "").Trim(),
            Quantity = Math.Clamp(quantity, 1, 999), AddedBy = by
        };
        await using var tx = await db.Database.BeginTransactionAsync();
        db.QuoteLines.Add(line);
        if (part.Length > 0) item.LastPartNumber = part;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = ticketId, Author = by, IsSystem = true, Stage = t.Stage,
            Body = $"Added to the quote: {Describe(line)}" + (photos.Count > 0 ? $", with {Photos(photos.Count)}." : ".") });
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        foreach (var p in photos) p.QuoteLineId = line.Id;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        line.PhotoIds = photos.OrderBy(p => p.Id).Select(p => p.Id).ToList();
        line.PhotoTokens = photos.OrderBy(p => p.Id).Select(p => p.Token).ToList();
        return (line, "");
    }

    /// <summary>
    /// More photos for a line already on the quote — the one somebody remembered after pressing Add.
    /// Only while the lines may be changed, as adding a line is.
    /// </summary>
    public async Task<string> AddLinePhotosAsync(int tenantId, long lineId, IEnumerable<long>? photoIds, string by, TenantRole role)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.QuoteLines.FirstOrDefaultAsync(l => l.Id == lineId && l.TenantId == tenantId);
        if (line == null) return "That item is not on the quote any more.";
        var t = await db.Tickets.FirstAsync(x => x.Id == line.TicketId);
        if (!MayEditLines(t, role, by)) return "The quote's items can be changed on the bench, or at Quote needed.";
        int already = await db.TicketPhotos.CountAsync(p => p.TenantId == tenantId && p.QuoteLineId == lineId);
        var (photos, perr) = await ClaimPhotosAsync(db, tenantId, t.Id, photoIds, already);
        if (perr.Length > 0) return perr;
        if (photos.Count == 0) return "Choose a photo first.";
        foreach (var p in photos) p.QuoteLineId = line.Id;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = $"Added {Photos(photos.Count)} to {line.ItemName} on the quote." });
        t.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    private static string Photos(int n) => n == 1 ? "1 photo" : $"{n} photos";

    /// <summary>
    /// What a photo is called in the request: the item's number in the email, then which of its photos
    /// — "item-2.jpg" when it has one, "item-2-photo-3.jpg" when it has several. The same for the
    /// .eml's attachments and the web mail downloads, so the supplier can tell whose photo is whose.
    /// </summary>
    public static string PhotoFileName(int item, int photo, int ofPhotos, string extension) =>
        ofPhotos > 1 ? $"item-{item}-photo-{photo}{extension}" : $"item-{item}{extension}";

    public async Task<string> RemoveLineAsync(int tenantId, long lineId, string by, TenantRole role)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.QuoteLines.FirstOrDefaultAsync(l => l.Id == lineId && l.TenantId == tenantId);
        if (line == null) return "";
        var t = await db.Tickets.FirstAsync(x => x.Id == line.TicketId);
        if (!MayEditLines(t, role, by)) return "The quote's items can be changed on the bench, or at Quote needed.";
        db.QuoteLines.Remove(line);
        // Its photos stay on the ticket, free to go on another item.
        foreach (var p in await db.TicketPhotos.Where(p => p.TenantId == tenantId && p.QuoteLineId == lineId).ToListAsync())
            p.QuoteLineId = null;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = $"Removed from the quote: {Describe(line)}." });
        await db.SaveChangesAsync();
        return "";
    }

    public static string Describe(QuoteLine l) =>
        (l.Quantity > 1 ? $"{l.Quantity} × " : "") + l.ItemName + (l.PartNumber.Length > 0 ? $", part number {l.PartNumber}" : "")
        + (l.SerialNumber.Length > 0 ? $", serial number {l.SerialNumber}" : "");

    // ---------------------------------------------------------------- the machine a quote is for

    /// <summary>The three kinds of machine (Clint, 2026-09-29: "Lets have all three options").</summary>
    public static readonly string[] MachineTypes = { "Desktop", "Laptop", "All-in-one" };

    /// <summary>
    /// The machine a quote is for. Once somebody has saved it, what they saved — blanks included,
    /// because "these parts are not for a machine" is an answer (Clint: "make those optional so that
    /// ... multiple items, that are not necessarily linked to a machine, that is also possible").
    /// Until then, a workshop job's quote is for the machine it was booked in with.
    /// </summary>
    public static QuoteMachine MachineOf(Ticket t) =>
        t.QuoteMachineSaved || t.Category != TicketCategory.Workshop
            ? new(t.QuoteMachineType, t.QuoteMachineMake, t.QuoteMachineModel, t.QuoteMachineSerial)
            : new("", t.AssetMake, t.AssetModel, t.AssetSerial);

    /// <summary>Says which machine a quote is for, or that it is not for one. Where the lines may be changed, by the same people.</summary>
    public async Task<string> SetMachineAsync(int tenantId, int ticketId, string? type, string? make, string? model, string? serial, string by, TenantRole role)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return "That ticket is not on file.";
        if (!MayEditLines(t, role, by)) return "The machine can be changed where the quote's items can: on the bench, or at Quote needed.";
        var kind = MachineTypes.FirstOrDefault(k => k.Equals((type ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        var m = new QuoteMachine(kind, Clean(make), Clean(model), Clean(serial));
        if (t.QuoteMachineSaved && m == MachineOf(t)) return "";
        t.QuoteMachineSaved = true;
        (t.QuoteMachineType, t.QuoteMachineMake, t.QuoteMachineModel, t.QuoteMachineSerial) = (m.Type, m.Make, m.Model, m.Serial);
        t.UpdatedUtc = DateTime.UtcNow;
        db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
            Body = m.IsEmpty ? "The quote is not for a particular machine." : $"The quote is for: {m.Describe()}." });
        await db.SaveChangesAsync();
        return "";
    }

    private static string Clean(string? s) => string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- suppliers

    public async Task<List<Supplier>> SuppliersAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Suppliers.Where(s => s.TenantId == tenantId && s.RemovedUtc == null).OrderBy(s => s.Name).ToListAsync();
    }

    /// <summary>
    /// Adds a supplier — or does not, and says why. The item list's guard: the same words in any
    /// order are the same supplier, and so is the same address. A name that merely resembles one
    /// already there is asked about ("did you mean") unless <paramref name="confirmed"/>.
    /// </summary>
    public async Task<(Supplier? supplier, string error, List<Supplier> similar)> AddSupplierAsync(int tenantId, string? name, string? email,
        string? contactPerson, string? phone, string by, TenantRole role, bool confirmed = false)
    {
        if (!Permissions.CanEditSuppliers(role)) return (null, "Suppliers are added by the office administrator, a supervisor or the owner.", new());
        name = Clean(name); email = (email ?? "").Trim();
        var key = MatchKey(name);
        if (key.Length == 0) return (null, "Name the supplier.", new());
        if (!TicketService.LooksLikeEmail(email)) return (null, "Enter the supplier's email address.", new());
        await using var db = await _dbf.CreateDbContextAsync();
        var live = await db.Suppliers.Where(s => s.TenantId == tenantId && s.RemovedUtc == null).ToListAsync();
        var same = live.FirstOrDefault(s => s.MatchKey == key);
        if (same != null) return (null, $"\"{same.Name}\" is already on the list.", new());
        var sameMail = live.FirstOrDefault(s => s.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (sameMail != null) return (null, $"{email} is already on the list, as \"{sameMail.Name}\".", new());
        var similar = live.Where(s => Resembles(s.Name, name)).OrderBy(s => s.Name).Take(5).ToList();
        if (similar.Count > 0 && !confirmed)
            return (null, "That looks like a supplier already on the list. Use one of these, or add it anyway if it really is different.", similar);
        var s = new Supplier { TenantId = tenantId, Name = name, MatchKey = key, Email = email, ContactPerson = Clean(contactPerson), Phone = Clean(phone), CreatedBy = by };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync();
        return (s, "", new());
    }

    public async Task<string> UpdateSupplierAsync(int tenantId, int id, string? name, string? email, string? contactPerson, string? phone, TenantRole role)
    {
        if (!Permissions.CanEditSuppliers(role)) return "Suppliers are changed by the office administrator, a supervisor or the owner.";
        name = Clean(name); email = (email ?? "").Trim();
        var key = MatchKey(name);
        if (key.Length == 0) return "Name the supplier.";
        if (!TicketService.LooksLikeEmail(email)) return "Enter the supplier's email address.";
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.RemovedUtc == null);
        if (s == null) return "That supplier is not on the list.";
        var others = await db.Suppliers.Where(x => x.TenantId == tenantId && x.RemovedUtc == null && x.Id != id).ToListAsync();
        if (others.FirstOrDefault(x => x.MatchKey == key) is { } clash) return $"\"{clash.Name}\" already has that name.";
        if (others.FirstOrDefault(x => x.Email.Equals(email, StringComparison.OrdinalIgnoreCase)) is { } mail) return $"{email} is already on the list, as \"{mail.Name}\".";
        (s.Name, s.MatchKey, s.Email, s.ContactPerson, s.Phone) = (name, key, email, Clean(contactPerson), Clean(phone));
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>Takes a supplier off the list. Kept on file: tickets that asked them still say so.</summary>
    public async Task<string> RemoveSupplierAsync(int tenantId, int id, TenantRole role)
    {
        if (!Permissions.CanEditSuppliers(role)) return "Suppliers are removed by the office administrator, a supervisor or the owner.";
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.RemovedUtc == null);
        if (s == null) return "";
        s.RemovedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>The company's quote request settings: the To address for a request to several suppliers, and the delivery address.</summary>
    public async Task<string> SaveQuoteSettingsAsync(int tenantId, string? toAddress, string? deliveryAddress, TenantRole role)
    {
        if (!Permissions.CanEditSuppliers(role)) return "These are set by the office administrator, a supervisor or the owner.";
        toAddress = (toAddress ?? "").Trim();
        if (toAddress.Length > 0 && !TicketService.LooksLikeEmail(toAddress)) return "The To address is not an email address.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstAsync(x => x.Id == tenantId);
        t.QuoteToAddress = toAddress;
        t.QuoteDeliveryAddress = string.Join("\n", (deliveryAddress ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>The addresses in a stored or posted list: split on commas and semicolons, trimmed, each once.</summary>
    public static List<string> Addresses(string? joined) =>
        (joined ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Who the request is addressed to (Clint, 2026-09-29). One supplier: To. Several: every one in
    /// BCC, so no supplier sees who else was asked, and the company's own address in To — a mail with
    /// no To at all is treated as spam. <see cref="Tenant.QuoteToAddress"/>, or the support address
    /// when that has not been set.
    /// </summary>
    public static (string to, string bcc) Addressing(Tenant tenant, string? suppliers)
    {
        var all = Addresses(suppliers);
        if (all.Count <= 1) return (all.FirstOrDefault() ?? "", "");
        return (OwnAddress(tenant), string.Join(", ", all));
    }

    /// <summary>The company's own address for the To line: what they set, or their support address.</summary>
    public static string OwnAddress(Tenant tenant) =>
        tenant.QuoteToAddress.Trim().Length > 0 ? tenant.QuoteToAddress.Trim() : tenant.SupportEmail.Trim();

    // ---------------------------------------------------------------- the quote request email

    /// <summary>
    /// The words of the request, the same for every kind of mail program. A signature with words is the
    /// whole closing (Clint, 2026-10-06: "our text may read Kind Regards, with the signature image directly
    /// below") — no "Thank you," of ours above it. Otherwise "Thank you,", the person's name and the company's.
    /// </summary>
    public static (string subject, string body) QuoteRequestText(Tenant tenant, Ticket t, IReadOnlyList<QuoteLine> lines, string fromPerson, QuoteSignature? signature = null)
    {
        var (subject, words) = QuoteRequestWords(tenant, t, lines);
        var closing = signature != null && QuoteSignatures.HasText(signature)
            ? QuoteSignatures.Plain(signature)
            : $"Thank you,\r\n{fromPerson}\r\n{tenant.CompanyName}\r\n";
        return (subject, words + closing);
    }

    /// <summary>
    /// The request as HTML, for a draft signed with a signature: the same words, then the signature
    /// drawn as it was set up. The image is <c>cid:</c>, carried inside the draft.
    /// </summary>
    public static string QuoteRequestHtml(Tenant tenant, Ticket t, IReadOnlyList<QuoteLine> lines, QuoteSignature signature) =>
        QuoteSignatures.EmailHtml(QuoteRequestWords(tenant, t, lines).words, QuoteSignatures.Html(signature, "cid:" + QuoteSignatures.ContentId));

    /// <summary>
    /// Everything before the closing, ending at "Our reference" — what web mail's compose window is given when a
    /// signature is pasted in after it (no web mail takes formatting through a link).
    /// </summary>
    public static (string subject, string words) QuoteRequestWords(Tenant tenant, Ticket t, IReadOnlyList<QuoteLine> lines)
    {
        var machine = MachineOf(t);
        var named = machine.Name;
        var subject = $"Quote request — job #{t.Number}" + (named.Length > 0 ? $" — {named}" : "");
        var sb = new StringBuilder();
        sb.Append("Hi,\r\n\r\nPlease could you quote us for the following");
        sb.Append(machine.IsEmpty ? ":\r\n\r\n" : $", for this machine:\r\n\r\n{machine.Describe()}\r\n\r\n");
        int i = 1;
        foreach (var l in lines)
        {
            sb.Append($"{i++}. {l.ItemName}");
            if (l.PartNumber.Length > 0) sb.Append($" — part number {l.PartNumber}");
            if (l.SerialNumber.Length > 0) sb.Append($" — serial number {l.SerialNumber}");
            sb.Append($" — quantity {l.Quantity}");
            if (l.PhotoIds.Count == 1) sb.Append(" (photo attached)");
            else if (l.PhotoIds.Count > 1) sb.Append($" ({l.PhotoIds.Count} photos attached)");
            sb.Append("\r\n");
        }
        // Clint: "the template should include asking what the delivery fee would be" — and, 2026-10-06,
        // "add to the template that it asks for eta", then "also add stock availability to the template".
        var delivery = tenant.QuoteDeliveryAddress.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        sb.Append(delivery.Length > 0
            ? $"\r\n{AskDeliveryStockEta}, for delivery to:\r\n\r\n{string.Join("\r\n", delivery)}\r\n"
            : $"\r\n{AskDeliveryStockEta}.\r\n");
        sb.Append($"\r\nOur reference: job #{t.Number}.\r\n\r\n");
        return (subject, sb.ToString());
    }

    /// <summary>What every request asks besides the price, word for word.</summary>
    public const string AskDeliveryStockEta = "Please include the delivery fee, whether each item is in stock, and the ETA (how soon each item can be delivered)";

    /// <summary>
    /// A draft email as a .eml file: addressed, written, photos attached, and marked unsent so
    /// Outlook, Thunderbird and Windows Mail open it ready to send rather than as a received
    /// message. This is 3b, Clint's choice for now; the server sending it itself is a later release.
    /// No From line — the mail program uses the account it is signed in to. <paramref name="bcc"/>
    /// is every supplier when several are asked (<see cref="Addressing"/>).
    /// </summary>
    /// <remarks>Signed with a signature (2026-10-06), it carries an HTML version beside the plain words —
    /// <paramref name="html"/> — and the signature's image inside it (<paramref name="inline"/>,
    /// <see cref="QuoteSignatures.ContentId"/>): mixed { alternative { plain, related { html, image } }, photos… }.
    /// Without one it is exactly what it always was.</remarks>
    public static byte[] QuoteRequestEml(string to, string subject, string body, IEnumerable<(string name, string type, byte[] bytes)> attachments, string bcc = "",
        string? html = null, (string type, byte[] bytes)? inline = null)
    {
        var boundary = "rsp-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append($"To: {Header(to)}\r\n");
        if (Header(bcc).Trim().Length > 0) sb.Append($"Bcc: {Header(bcc)}\r\n");
        sb.Append($"Subject: {EncodedWord(subject)}\r\n");
        sb.Append("X-Unsent: 1\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\n");
        if (html == null)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(body))));
        }
        else
        {
            var alt = "rsp-alt-" + Guid.NewGuid().ToString("N");
            sb.Append($"Content-Type: multipart/alternative; boundary=\"{alt}\"\r\n\r\n");
            sb.Append($"--{alt}\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(body))));
            sb.Append($"--{alt}\r\n");
            string? rel = null;
            if (inline is { } img)
            {
                rel = "rsp-rel-" + Guid.NewGuid().ToString("N");
                sb.Append($"Content-Type: multipart/related; type=\"text/html\"; boundary=\"{rel}\"\r\n\r\n");
                sb.Append($"--{rel}\r\n");
            }
            sb.Append("Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(html))));
            if (rel != null)
            {
                var (type, bytes) = inline!.Value;
                var name = "signature" + QuoteSignatures.ExtensionFor(type);
                sb.Append($"--{rel}\r\n");
                sb.Append($"Content-Type: {type}; name=\"{name}\"\r\n");
                sb.Append($"Content-ID: <{QuoteSignatures.ContentId}>\r\n");
                sb.Append($"Content-Disposition: inline; filename=\"{name}\"\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(Wrap(Convert.ToBase64String(bytes)));
                sb.Append($"--{rel}--\r\n");
            }
            sb.Append($"--{alt}--\r\n");
        }
        foreach (var (name, type, bytes) in attachments)
        {
            sb.Append($"--{boundary}\r\n");
            sb.Append($"Content-Type: {type}; name=\"{name}\"\r\n");
            sb.Append($"Content-Disposition: attachment; filename=\"{name}\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(bytes)));
        }
        sb.Append($"--{boundary}--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// For a person whose mail lives in a browser, which cannot open a draft file: their compose
    /// window, already addressed and written. The photos are downloaded beside it to attach by hand
    /// — no web mail accepts an attachment through a link.
    /// </summary>
    public static string ComposeUrl(string mailClient, string to, string subject, string body, string bcc = "")
    {
        var b = Header(bcc).Trim();
        return mailClient switch
        {
            MailClients.Gmail => "https://mail.google.com/mail/?view=cm&fs=1" +
                $"&to={Uri.EscapeDataString(to)}" + (b.Length > 0 ? $"&bcc={Uri.EscapeDataString(b)}" : "") +
                $"&su={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}",
            MailClients.OutlookWeb => "https://outlook.office.com/mail/deeplink/compose" +
                $"?to={Uri.EscapeDataString(to)}" + (b.Length > 0 ? $"&bcc={Uri.EscapeDataString(b)}" : "") +
                $"&subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}",
            _ => ""
        };
    }

    private static string Header(string s) => new string((s ?? "").Where(c => c is not ('\r' or '\n')).ToArray());
    private static string EncodedWord(string s) => s.All(c => c < 128) ? Header(s) : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(Header(s)))}?=";
    private static string Wrap(string b64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>The machine a quote is for. Every part optional; all blank means "not for a machine".</summary>
public sealed record QuoteMachine(string Type, string Make, string Model, string Serial)
{
    public bool IsEmpty => Type.Length == 0 && Make.Length == 0 && Model.Length == 0 && Serial.Length == 0;
    /// <summary>"Asus VivoBook 123" — make and model, or the type alone when neither was given.</summary>
    public string Name => $"{Make} {Model}".Trim() is { Length: > 0 } mm ? mm : Type;
    /// <summary>"Laptop — Asus VivoBook 123 — serial number SN123", leaving out whatever was not given.</summary>
    public string Describe() => string.Join(" — ", new[]
    {
        Type, $"{Make} {Model}".Trim(), Serial.Length > 0 ? $"serial number {Serial}" : ""
    }.Where(s => s.Length > 0));
}

/// <summary>
/// Which mail a person uses, asked once the first time they request a quote and remembered on
/// them (Clint, 2026-09-24: "we need to consider users of Gmail and Outlook online, maybe a one
/// step check before creating the email").
/// </summary>
public static class MailClients
{
    public const string Desktop = "desktop", Gmail = "gmail", OutlookWeb = "outlook-web";
    public static bool Valid(string? s) => s is Desktop or Gmail or OutlookWeb;
    public static string Label(string s) => s switch
    {
        Desktop => "Outlook or another mail program on this computer",
        Gmail => "Gmail",
        OutlookWeb => "Outlook on the web (Microsoft 365)",
        _ => "Not chosen yet"
    };
}
