namespace RemoteSupport.Platform.Services;

/// <summary>
/// Caddy reaches the platform through a Unix socket (2026-10-13, BUGS #12). Over TCP to 127.0.0.1:8090 every proxied
/// WebSocket held one of Linux's ~28,000 local ports for its whole life; the xneelo load test (HANDOVER §126) ran out at
/// ~27 companies and then EVERY request was answered 502 — the portal and the consoles dead for everybody. A socket
/// file uses no ports. go-live.sh lists it in <c>Urls</c> beside 127.0.0.1:8090, which stays for the deploy, switch,
/// status and backup checks on the server itself.
///
/// <para>Kestrel makes the file with the process's umask (0022: only the owner may connect, and connecting needs
/// WRITE). Once it is listening this lets its own group in too — and nobody else; <c>caddy</c> is a member of group
/// <c>rsp</c>, and systemd's <c>RuntimeDirectory=rsp</c> (0750) keeps the folder to the same two.</para>
/// </summary>
public static class UnixSocket
{
    private const string Prefix = "http://unix:";

    /// <summary>
    /// Who is calling, behind Caddy (§72): X-Forwarded-For and -Proto, one hop, trusted from loopback (the default).
    /// <para>A connection through the SOCKET has no remote address. ASP.NET then takes the first forwarder as given
    /// ("servers that don't support it natively") — which is right here only because nothing but Caddy can open the
    /// socket (group rsp, 0660, /run/rsp 0750). Were it otherwise, every caller would be "?": ONE address for the
    /// sign-in guard's 30-per-address lock, so thirty wrong passwords from anybody would lock out everybody.
    /// <c>tools/unixsockcheck</c> holds both halves.</para>
    /// </summary>
    public static void TrustCaddy(Microsoft.AspNetCore.Builder.ForwardedHeadersOptions o)
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                           | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
    }

    /// <summary>The socket paths among Kestrel's addresses, as written in <c>Urls</c>.</summary>
    public static IEnumerable<string> PathsIn(IEnumerable<string> urls) =>
        urls.Where(u => u.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).Select(u => u[Prefix.Length..]);

    public static void OpenToGroup(IEnumerable<string> urls, ILogger log)
    {
        foreach (var path in PathsIn(urls))
        {
            // On Windows (development) a socket file has no such modes; the address is all there is to it.
            if (OperatingSystem.IsWindows()) { log.LogInformation("Listening on unix socket {Path}", path); continue; }
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                log.LogInformation("Listening on unix socket {Path} (owner and group only)", path);
            }
            // Said loudly: with this failing Caddy cannot connect, and every page answers 502.
            catch (Exception ex) { log.LogError(ex, "Could not let Caddy (group rsp) into the unix socket {Path}", path); }
        }
    }
}
