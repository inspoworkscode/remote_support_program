namespace RemoteSupport.Platform.Data;

public enum AccountTokenPurpose { Invite = 0, Reset = 1 }

/// <summary>
/// A one-use link that lets somebody choose their own password (2026-10-01, HANDOVER §73): an
/// INVITATION when they are added — nobody types a password for anybody any more — or a RESET from
/// "Forgot password?" or a supervisor's Send reset link.
///
/// Only the SHA-256 of the link's secret is kept, so the table is no use to whoever reads it. Each
/// is spent by the POST that sets the password, never by opening the page: mail filters (Microsoft
/// Safe Links and the like) open every link in a message before the person does, and a link spent
/// by that would arrive already used. A new link for the same person and purpose replaces the old.
/// </summary>
public sealed class AccountToken
{
    public long Id { get; set; }
    public int TechnicianId { get; set; }
    public AccountTokenPurpose Purpose { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    /// <summary>Who asked for it: a supervisor's name, "Forgot password", or "command line".</summary>
    public string CreatedBy { get; set; } = "";
}
