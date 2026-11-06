namespace RemoteSupport.Platform.Data;

/// <summary>
/// A company accepting the terms at signup (2026-10-05): the region's terms WORD FOR WORD as they
/// were shown, the version and its fingerprint, who ticked the box, from where, and when. Never
/// changed or deleted while the company exists — it is the answer to "what did they agree to?",
/// however the terms files have changed since. Written by <see cref="Services.TenantService.SignupAsync"/>
/// in the same save as the company.
/// </summary>
public sealed class TermsAcceptance
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Region { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>True when the text accepted was still marked DRAFT (not yet approved by a lawyer).</summary>
    public bool Draft { get; set; }
    /// <summary>The terms as shown, in the knowledge base's markup.</summary>
    public string Text { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string AcceptedByName { get; set; } = "";
    public string AcceptedByEmail { get; set; } = "";
    /// <summary>The address the signup came from, as the server saw it behind the proxy.</summary>
    public string FromAddress { get; set; } = "";
    public DateTime AcceptedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Somebody who wants to sign up in a region with no server yet (2026-10-05, Clint's answer: a waiting
/// list, not a place on another country's server). Only what is needed to tell them when it opens.
/// The platform owner sees the list on the owner portal and removes an entry once they are told.
///
/// <para>Since 2026-10-22 also somebody in an OPEN region who found every place taken (<see cref="Full"/>,
/// <see cref="Services.OpenPlaces"/>). An admin offers them a place: a link that works once, for
/// <see cref="Services.OpenPlaces.OfferDays"/> days, holding that place while it waits — only its fingerprint is kept.</para>
/// </summary>
public sealed class WaitingListEntry
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>They joined because every place was taken, not because their region has no server yet.</summary>
    public bool Full { get; set; }
    /// <summary><see cref="Services.OpenPlaces.Hash"/> of the place link's token, once a place is offered. Never the token.</summary>
    public string PlaceTokenHash { get; set; } = "";
    public DateTime? PlaceOfferedUtc { get; set; }
    public DateTime? PlaceExpiresUtc { get; set; }
    /// <summary>When a company was made with the place — set in the same save as the company, once.</summary>
    public DateTime? PlaceUsedUtc { get; set; }
    public int? TenantId { get; set; }
}
