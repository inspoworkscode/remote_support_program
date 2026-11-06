namespace RemoteSupport.Platform.Services;

/// <summary>
/// Which server a region's companies live on (2026-10-05, Clint: "The idea is to have a server in
/// every country that we are selling the remote support program"). A company's data stays on its
/// own region's server, so signup sends each region to its own — and a region with no server yet
/// gets a waiting list, never a place on another country's server (his answer to "until a country
/// has its own server").
///
/// <para>Configuration, per server:</para>
/// <list type="bullet">
/// <item><c>Platform:Region</c> — the region THIS server holds (ZA today).</item>
/// <item><c>Platform:RegionServers:{code}</c> — the public address of another region's server, once
/// it exists, e.g. <c>Platform:RegionServers:UK = https://uk.theremotesupportprogram.com</c>.
/// Signup on this server then sends that region's companies there.</item>
/// </list>
/// <para>Nothing is shared between servers: each is a whole platform with its own database. Moving a
/// company between them is not built, on purpose — nobody is put on the wrong one to begin with.</para>
/// </summary>
public sealed class RegionServers
{
    private readonly IConfiguration _cfg;
    public RegionServers(IConfiguration cfg) => _cfg = cfg;

    /// <summary>The region this server holds.</summary>
    public string Here => Pricing.For(_cfg["Platform:Region"]).Code;

    public enum Place { Here, Elsewhere, NotYet }

    /// <summary>Where a region's companies sign up: here, at another server's address, or not yet anywhere.</summary>
    public (Place place, string url) Where(string? region)
    {
        var code = Pricing.For(region).Code;
        if (code == Here) return (Place.Here, "");
        var url = (_cfg[$"Platform:RegionServers:{code}"] ?? "").Trim().TrimEnd('/');
        // Never send anybody to a plain-http address across the internet (SecureTransport, §72).
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? (Place.Elsewhere, url) : (Place.NotYet, "");
    }
}
