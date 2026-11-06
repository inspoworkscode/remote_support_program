namespace TestChecklist;

static class Program
{
    /// TestChecklist.exe [--root folder] [--me name]
    /// Without --root, the folder is the one the exe is in — the shared drive.
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string root = AppContext.BaseDirectory;
        string? me = null, snapshot = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--root") root = Path.GetFullPath(args[++i]);
            else if (args[i] == "--me") me = args[++i];
            else if (args[i] == "--snapshot") snapshot = Path.GetFullPath(args[++i]);
        }

        if (!File.Exists(Path.Combine(root, "checks.md")))
        {
            MessageBox.Show($"There is no checks.md beside the program, in\n\n{root}\n\n" +
                            "It is the testing checklist. Publish-TestChecklist.ps1 puts it there.",
                            "Test Checklist", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        me ??= Identity.Load() ?? Identity.Ask(null, null);
        if (me == null) return;

        var form = new MainForm(root, me);
        if (snapshot != null) form.Shown += (_, _) => Snapshot.Run(form, snapshot);
        Application.Run(form);
    }
}

/// Who is testing, remembered per Windows account on each PC.
static class Identity
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestChecklist", "me.txt");

    public static string? Load()
    {
        try
        {
            var s = File.ReadAllText(FilePath).Trim();
            return s.Length > 0 ? s : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string? Ask(IWin32Window? owner, string? current)
    {
        using var d = new NameDialog(current);
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, d.Value);
        }
        catch (IOException) { }
        return d.Value;
    }
}
