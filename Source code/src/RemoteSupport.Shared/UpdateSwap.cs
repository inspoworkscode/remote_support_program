using System.Text.Json;

namespace RemoteSupport.Shared;

/// <summary>
/// Putting a release's files in place on a machine, and taking them out again (2026-10-16, auto-update stage D).
///
/// <para><b>Windows lets you RENAME a running program but not overwrite it.</b> So each file the release replaces is
/// renamed aside (<c>*.old</c>) and the new one copied into its place: the tray app, perhaps running for every person
/// signed in, carries on from its renamed file until it restarts into the new one. Nothing has to be stopped first except
/// the agent, which must restart to run its new self.</para>
///
/// <para><b>Only the release's own files are touched</b> (<see cref="ReleaseFiles"/>): the three programs, the two install
/// scripts and the health check's scripts. Never <c>branding\</c>, <c>config.json</c>, <c>device.json</c> or anything else
/// in the folder — and nothing is ever deleted that the release did not put there.</para>
///
/// <para><see cref="Apply"/> writes what it did to <c>update\applied.json</c> BEFORE it does it, so <see cref="RollBack"/>
/// can undo it — even half-done, even from a later process. <see cref="Commit"/> then forgets the old files.</para>
/// </summary>
public static class UpdateSwap
{
    public const string UpdateFolder = "update";
    public const string AppliedFile = "applied.json";
    public const string OldSuffix = ".old";
    /// <summary>A NEW file that had to make way in a rollback while it was still running.</summary>
    public const string BadSuffix = ".bad";

    /// <summary>The programs and scripts a release lays down at the top of the install folder, by name.</summary>
    public static readonly string[] TopFiles =
    {
        "RemoteSupport.Client.exe", "RemoteSupport.Host.exe", "RemoteSupport.Agent.exe", "Install-Client.ps1", "Uninstall-Client.ps1"
    };

    /// <summary>The programs among them, which must each carry the release's own version.</summary>
    public static bool IsProgram(string rel) => rel.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files of an unpacked release that may be installed, as paths relative to it: the <see cref="TopFiles"/> it has,
    /// and every <c>healthcheck\*.ps1</c>. Anything else in it — its default branding, the .bat files, the read-me — is not
    /// the machine's to take.
    /// </summary>
    public static List<string> ReleaseFiles(string unpacked)
    {
        var list = TopFiles.Where(f => File.Exists(Path.Combine(unpacked, f))).ToList();
        var hc = Path.Combine(unpacked, "healthcheck");
        if (Directory.Exists(hc))
            list.AddRange(Directory.EnumerateFiles(hc, "*.ps1", SearchOption.TopDirectoryOnly)
                .Select(f => Path.Combine("healthcheck", Path.GetFileName(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>What <see cref="Apply"/> did, so it can be undone.</summary>
    public sealed class Applied
    {
        public string Version { get; set; } = "";
        public DateTime AtUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Every file put in place, relative to the install folder.</summary>
        public List<string> Files { get; set; } = new();
        /// <summary>Of them, those that replaced a file — renamed aside to *.old. The rest were new.</summary>
        public List<string> Replaced { get; set; } = new();
    }

    public static string AppliedPath(string installDir) => Path.Combine(installDir, UpdateFolder, AppliedFile);

    /// <summary>
    /// Puts <paramref name="files"/> from <paramref name="staged"/> into <paramref name="installDir"/>, the old ones renamed
    /// aside. A failure part-way throws — the caller rolls back, and <c>applied.json</c> already says what was done.
    /// </summary>
    public static void Apply(string installDir, string staged, IReadOnlyList<string> files, string version)
    {
        var record = new Applied { Version = version };
        Directory.CreateDirectory(Path.Combine(installDir, UpdateFolder));
        foreach (var rel in files)
        {
            Inside(installDir, rel);
            var target = Path.Combine(installDir, rel);
            var from = Path.Combine(staged, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            bool had = File.Exists(target);
            // Said BEFORE it is done: a crash between the rename and the copy must still be undone.
            record.Files.Add(rel);
            if (had) record.Replaced.Add(rel);
            Save(installDir, record);
            if (had)
            {
                var old = target + OldSuffix;
                if (File.Exists(old)) TryDelete(old, rename: true);
                File.Move(target, old);
            }
            File.Copy(from, target, overwrite: true);
            // Copied, not downloaded: the downloaded-from-the-internet mark must never reach Program Files (§21).
            TryDelete(target + ":Zone.Identifier", rename: false);
        }
        Save(installDir, record);
    }

    /// <summary>
    /// Undoes <see cref="Apply"/> from its record: every replaced file back from *.old, every new file taken out. A new
    /// file still running is renamed aside (*.bad) rather than deleted. Returns what it could not undo, or empty.
    /// </summary>
    public static List<string> RollBack(string installDir)
    {
        var problems = new List<string>();
        var record = Load(installDir);
        if (record == null) return problems;
        foreach (var rel in Enumerable.Reverse(record.Files))
        {
            var target = Path.Combine(installDir, rel);
            var old = target + OldSuffix;
            try
            {
                bool wasReplaced = record.Replaced.Contains(rel);
                if (wasReplaced && !File.Exists(old)) continue;   // never renamed aside: the original is still in place
                if (File.Exists(target)) TryDelete(target, rename: true);
                if (wasReplaced) File.Move(old, target);
            }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        if (problems.Count == 0) TryDelete(AppliedPath(installDir), rename: false);
        return problems;
    }

    /// <summary>The update stays: the old files are no longer needed. One still running (a tray app not yet restarted) stays
    /// until the next update clears it.</summary>
    public static void Commit(string installDir)
    {
        var record = Load(installDir);
        if (record != null)
            foreach (var rel in record.Replaced) TryDelete(Path.Combine(installDir, rel) + OldSuffix, rename: false);
        TryDelete(AppliedPath(installDir), rename: false);
        ClearLeftovers(installDir);
    }

    /// <summary>*.old and *.bad files from earlier updates that were still running then. Best effort.</summary>
    public static void ClearLeftovers(string installDir)
    {
        foreach (var rel in TopFiles.Concat(new[] { "healthcheck" }))
        {
            var path = Path.Combine(installDir, rel);
            var dir = Directory.Exists(path) ? path : installDir;
            var pattern = Directory.Exists(path) ? "*" : rel + "*";
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                    if (f.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase) || f.EndsWith(BadSuffix, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Contains(BadSuffix + ".", StringComparison.OrdinalIgnoreCase))
                        TryDelete(f, rename: false);
            }
            catch { }
        }
    }

    public static Applied? Load(string installDir)
    {
        try { return File.Exists(AppliedPath(installDir)) ? JsonSerializer.Deserialize<Applied>(File.ReadAllText(AppliedPath(installDir))) : null; }
        catch { return null; }
    }

    private static void Save(string installDir, Applied record)
    {
        var path = AppliedPath(installDir);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>A relative path that stays inside the install folder — a release never names anything outside it.</summary>
    private static void Inside(string installDir, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(installDir, rel));
        var root = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(rel) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{rel} is not inside the install folder");
    }

    /// <summary>Deletes a file; one that is running cannot be deleted, so with <paramref name="rename"/> it is moved aside instead.</summary>
    private static void TryDelete(string path, bool rename)
    {
        try { if (File.Exists(path)) File.Delete(path); return; }
        catch (Exception) when (rename)
        {
            File.Move(path, path + BadSuffix + "." + DateTime.UtcNow.Ticks.ToString("x"));
        }
        catch { }
    }
}
