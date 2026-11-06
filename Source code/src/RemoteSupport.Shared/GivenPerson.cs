using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteSupport.Shared;

/// <summary>
/// The person a machine was installed for — name, surname and email, typed into the installer window (2026-10-23).
///
/// <para>Clint: <i>"during the client install, the client has to put in their name, surname and email address. This will
/// then be the default name that shows up in the console and when they then request support ... And then if one links said
/// user to a client, it automatically registers it as a contact under that client."</i> §150 already made the name typed
/// at a support request the machine's name until somebody links it; this starts that at the install, with an email.</para>
///
/// <para>The installer window writes <see cref="FileName"/> into the folder it installs from, so Install-Client.ps1 copies it
/// into Program Files with everything else; the agent and the tray app read it from where they run and say it in every
/// hello. The server keeps it ONCE per <see cref="Id"/> (Device.InstalledForId): after that a support request with another
/// name wins, as §150's "the latest name wins" says — a hello that repeated it would put the installer's name back every
/// time the machine connected. A scripted install (no window) writes no file and the machine behaves as before.</para>
/// </summary>
public sealed class GivenPerson
{
    public const string FileName = "person.json";
    /// <summary>The longest email kept. Long enough for anybody's; the box is free text.</summary>
    public const int EmailMax = 200;

    /// <summary>A new one for every install that typed different details — the server keeps each once.</summary>
    public string Id { get; set; } = "";
    /// <summary>"Jane Smith": the two boxes, joined and tidied as a name from a request is (<see cref="OwnerNames.Clean"/>).</summary>
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string First { get; set; } = "";
    public string Surname { get; set; } = "";

    /// <summary>The two boxes as one name — contacts have always been one name, and stay so.</summary>
    public static string Join(string? first, string? surname) => OwnerNames.Clean($"{first} {surname}");

    /// <summary>The same shape the platform has always asked of an address (TicketIntake.LooksLikeEmail).</summary>
    public static bool LooksLikeEmail(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= EmailMax
        && Regex.IsMatch(s.Trim(), @"^[^@\s<>,;""]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$");

    /// <summary>What is kept of a typed email: trimmed, and only if it looks like one — never half an address.</summary>
    public static string CleanEmail(string? typed) => LooksLikeEmail(typed) ? typed!.Trim() : "";

    /// <summary>What stops the installer: all three are needed, the email only has to look like one. Empty: nothing.</summary>
    public static string Problem(string? first, string? surname, string? email)
    {
        if (string.IsNullOrWhiteSpace(first)) return "Type your name.";
        if (string.IsNullOrWhiteSpace(surname)) return "Type your surname.";
        if (string.IsNullOrWhiteSpace(email)) return "Type your email address.";
        if (!LooksLikeEmail(email)) return "That email address does not look right.";
        return "";
    }

    /// <summary>
    /// The details as they will be saved: a new <see cref="Id"/> unless they are the same as <paramref name="before"/> — an
    /// Update that leaves the boxes as they were must not put this name back over a later support request's.
    /// </summary>
    public static GivenPerson Make(string first, string surname, string email, GivenPerson? before)
    {
        var p = new GivenPerson
        {
            First = OwnerNames.Clean(first), Surname = OwnerNames.Clean(surname),
            Name = Join(first, surname), Email = CleanEmail(email)
        };
        p.Id = before != null && before.Id.Length > 0 && before.Name == p.Name
               && string.Equals(before.Email, p.Email, StringComparison.OrdinalIgnoreCase)
            ? before.Id : Guid.NewGuid().ToString("N");
        return p;
    }

    /// <summary>The details in <paramref name="folder"/>, or null when there are none (a scripted install) or they cannot be read.</summary>
    public static GivenPerson? Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return null;
            var p = JsonSerializer.Deserialize<GivenPerson>(File.ReadAllText(path));
            if (p == null) return null;
            p.Name = OwnerNames.Clean(p.Name);
            p.Email = CleanEmail(p.Email);
            return p.Id.Length > 0 && p.Name.Length > 0 ? p : null;
        }
        catch { return null; }
    }

    /// <summary>The details of the install this program runs from.</summary>
    public static GivenPerson? Installed() => Load(AppContext.BaseDirectory);

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this));
    }
}
