namespace RemoteSupport.Platform.Data;

/// <summary>
/// A company's owner agreeing to a higher monthly price (2026-10-05, Clint: "it should prompt them
/// and show an increase in the price which they must accept or reject"). One row each time; never
/// changed, never deleted — it is the record that they said yes, to what figure, and when, for the
/// day an invoice is queried. Written by <see cref="Services.TenantService"/> only, in the same save
/// as the change it paid for.
/// </summary>
public sealed class SeatAgreement
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>People in seats once the change is made, and how many the package includes.</summary>
    public int People { get; set; }
    public int Included { get; set; }
    /// <summary>Region code and the new monthly total in that region's money, as agreed.</summary>
    public string Region { get; set; } = "";
    public decimal Monthly { get; set; }
    /// <summary>The monthly total before, for reading the row on its own.</summary>
    public decimal MonthlyBefore { get; set; }
    /// <summary>What it was for, in words: "Added Sam Smith (Technician)".</summary>
    public string What { get; set; } = "";
    /// <summary>Who agreed — the owner's name and email — or "the platform (command line)".</summary>
    public string AgreedBy { get; set; } = "";
    public DateTime AgreedUtc { get; set; } = DateTime.UtcNow;
}
