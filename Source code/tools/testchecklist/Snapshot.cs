namespace TestChecklist;

// TestChecklist.exe --root <folder> --me <name> --snapshot <out-dir>
// For checking the program itself, not for testers: dumps how checks.md was read, takes a picture
// of each screen as really drawn on this monitor, writes a report for today, then exits.

static class Snapshot
{
    public static void Run(MainForm f, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sb = new System.Text.StringBuilder();
        foreach (var s in f.Book.Sections)
        {
            sb.AppendLine($"[{s.Key}] {s.Title}  intro={s.Intro.Count} lines, checks={s.Checks.Count}");
            foreach (var c in s.Checks)
                sb.AppendLine($"   {c.Number,4}  {c.Short}" + (c.DoneInDoc != null ? $"   [DONE {c.DoneInDoc}]" : "") +
                              (c.ReplacedBy != null ? $"   [REPLACED BY {c.ReplacedBy}]" : "") + $"   ({c.Lines.Count} lines)");
        }
        File.WriteAllText(Path.Combine(outDir, "parsed.txt"), sb.ToString());

        var steps = new Queue<Action>();
        var tabs = f.Controls.OfType<TabControl>().First();
        var sections = Find<ListBox>(f).First();
        var list = Find<ListView>(tabs.TabPages[0]).First();

        steps.Enqueue(() => Snap(f, outDir, "1-home.png"));
        steps.Enqueue(() =>
        {
            for (int i = 0; i < sections.Items.Count; i++)
                if (sections.Items[i].ToString()!.StartsWith("A.")) sections.SelectedIndex = i;
        });
        steps.Enqueue(() => Snap(f, outDir, "2-section.png"));
        steps.Enqueue(() => { if (list.Items.Count > 1) list.Items[1].Selected = true; });
        steps.Enqueue(() => Snap(f, outDir, "3-check.png"));
        steps.Enqueue(() => tabs.SelectedIndex = 1);
        steps.Enqueue(() => { var l = Find<ListView>(tabs.TabPages[1]).First(); if (l.Items.Count > 0) l.Items[0].Selected = true; });
        steps.Enqueue(() => Snap(f, outDir, "4-faults.png"));
        Form? dlg = null;
        steps.Enqueue(() =>
        {
            dlg = new ItemDialog(Kinds.Fault, f.Book, f.AreasInUse(), null, "42", "Check 42: example", "What I saw");
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = f.Location + new Size(80, 80);
            dlg.Show(f);
        });
        steps.Enqueue(() => { Snap(dlg!, outDir, "5-fault-dialog.png"); dlg!.Close(); });
        steps.Enqueue(() =>
        {
            dlg = new ExportDialog { StartPosition = FormStartPosition.Manual, Location = f.Location + new Size(80, 80) };
            dlg.Show(f);
        });
        steps.Enqueue(() => { Snap(dlg!, outDir, "6-export-dialog.png"); dlg!.Close(); });
        if (f.Board.Items.Count > 0)
        {
            steps.Enqueue(() =>
            {
                dlg = new UpdateDialog(f.Board.Items.Values.First()) { StartPosition = FormStartPosition.Manual, Location = f.Location + new Size(80, 80) };
                dlg.Show(f);
            });
            steps.Enqueue(() => { Snap(dlg!, outDir, "7-update-dialog.png"); dlg!.Close(); });
        }
        steps.Enqueue(() =>
        {
            var today = DateTime.Today;
            Exporter.Write(f, Path.Combine(outDir, "report-today.md"), today, today.AddDays(1), today.ToString("yyyy-MM-dd"));
            Exporter.Write(f, Path.Combine(outDir, "report-all.md"), null, null, "everything");
        });
        steps.Enqueue(() =>
        {
            // A save goes to this person's own file and is seen on the next read.
            f.Append(new Entry { Type = "result", Check = "5", Status = "works", Note = "saved by the snapshot pass" });
            var anyFault = f.Board.ItemsOf(Kinds.Fault).FirstOrDefault();
            if (anyFault != null) f.Append(new Entry { Type = "update", Target = anyFault.Id, Status = "Closed", Comment = "closed by the snapshot pass" });
            f.Reload(true);
            var five = f.Board.Checks["5"];
            var f1 = anyFault == null ? null : f.Board.Items[anyFault.Id];
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"),
                $"check 5: {five.Status} by {five.By} — {(five.Status == "works" && five.By == f.Me ? "PASS" : "FAIL")}\n" +
                (f1 == null ? "no faults to close\n" : $"{f1.Code}: {f1.Status} — {(f1.Status == "Closed" ? "PASS" : "FAIL")}\n") +
                $"files: {string.Join(", ", Directory.GetFiles(f.Store.DataDir, "*.jsonl").Select(Path.GetFileName))}\n");
        });
        steps.Enqueue(() => f.Close());

        var t = new System.Windows.Forms.Timer { Interval = 700 };
        t.Tick += (_, _) => { if (steps.Count > 0) steps.Dequeue()(); else t.Stop(); };
        t.Start();
    }

    static IEnumerable<T> Find<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    static void Snap(Form f, string dir, string name)
    {
        f.Activate();
        f.Refresh();
        var b = f.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        bmp.Save(Path.Combine(dir, name));
    }
}
