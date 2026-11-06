using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;

namespace RemoteSupport.Media;

/// <summary>
/// Prints a file on a printer of this machine — remote printing, both directions (roadmap 1.8,
/// Clint's choice of "both", 2026-10-06). The console prints a customer's print job (a PDF) on the
/// technician's printer; the host's helper, running as the person, prints a technician's file on the
/// customer's printer.
///
/// <para>PDFs, pictures and plain text are drawn here, so nothing has to be installed to print them.
/// Anything else (a Word document, a spreadsheet) is handed to whatever program Windows prints that
/// kind of file with — the "printto" or "print" verb — and refused, in words, when there is none.</para>
/// </summary>
public static class DocumentPrinter
{
    public sealed record Result(bool Ok, string Printer, int Pages, string Error);

    private static readonly string[] Pictures = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
    private static readonly string[] Texts = { ".txt", ".log", ".csv", ".ini", ".xml", ".json", ".md" };

    /// <summary>This machine's default printer, or "" when it has none.</summary>
    public static string DefaultPrinter()
    {
        try { var s = new PrinterSettings(); return s.IsDefaultPrinter && s.IsValid ? s.PrinterName : ""; } catch { return ""; }
    }

    /// <summary>The printers on this machine, by name.</summary>
    public static List<string> Printers()
    {
        try { return PrinterSettings.InstalledPrinters.Cast<string>().ToList(); } catch { return new(); }
    }

    /// <summary>
    /// Prints <paramref name="path"/> on <paramref name="printer"/> (the default when empty) as a job
    /// called <paramref name="jobName"/>. <paramref name="toFile"/> is for harnesses: the job is written
    /// there instead of to paper (the printer's own output, e.g. "Microsoft Print to PDF" makes a PDF).
    /// </summary>
    public static Result Print(string path, string? printer, string jobName, string? toFile = null)
    {
        var target = string.IsNullOrWhiteSpace(printer) ? DefaultPrinter() : printer!;
        if (target.Length == 0) return new Result(false, "", 0, "this computer has no printer set up");
        if (!File.Exists(path)) return new Result(false, target, 0, "the file is not there");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pdf") return PrintPdf(path, target, jobName, toFile);
            if (Pictures.Contains(ext)) return PrintPicture(path, target, jobName, toFile);
            if (Texts.Contains(ext)) return PrintText(path, target, jobName, toFile);
            return PrintWithItsProgram(path, target);
        }
        catch (Exception ex) { return new Result(false, target, 0, ex.Message); }
    }

    private static PrintDocument Document(string printer, string jobName, string? toFile)
    {
        var doc = new PrintDocument { DocumentName = jobName, PrintController = new StandardPrintController() };
        doc.PrinterSettings.PrinterName = printer;
        if (!doc.PrinterSettings.IsValid) throw new InvalidOperationException($"there is no printer called \"{printer}\" here");
        if (toFile != null) { doc.PrinterSettings.PrintToFile = true; doc.PrinterSettings.PrintFileName = toFile; }
        return doc;
    }

    /// <summary>
    /// Fits a picture of <paramref name="inches"/> onto the page, centred, keeping its shape. A PDF made
    /// by "Print to PDF" already carries the original's margins, so it goes on the whole sheet; the
    /// printer's unprintable edge is all that is lost.
    /// </summary>
    private static void DrawFitted(PrintPageEventArgs e, Image img, SizeF inches, bool wholePage)
    {
        var g = e.Graphics!;
        RectangleF area = wholePage
            ? new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, e.PageBounds.Width, e.PageBounds.Height)
            : e.MarginBounds;
        float w = inches.Width * 100f, h = inches.Height * 100f;              // hundredths of an inch
        float scale = Math.Min(area.Width / w, area.Height / h);
        if (!wholePage) scale = Math.Min(scale, 1f);                           // never blow a small picture up
        var dest = new RectangleF(area.X + (area.Width - w * scale) / 2, area.Y + (area.Height - h * scale) / 2, w * scale, h * scale);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, dest);
    }

    private static Result PrintPdf(string path, string printer, string jobName, string? toFile)
    {
        using var pdf = new PdfPages(path);
        if (pdf.Count == 0) return new Result(false, printer, 0, "the document has no pages");
        using var doc = Document(printer, jobName, toFile);
        int next = 0;
        doc.QueryPageSettings += (_, e) =>
        {
            var s = pdf.PageInches(next);
            e.PageSettings.Landscape = s.Width > s.Height;
        };
        doc.PrintPage += (_, e) =>
        {
            // 200 dpi: sharp on paper, and an A4 page is 30 MB of memory rather than 70 at 300.
            using var page = pdf.Render(next, 200);
            DrawFitted(e, page, pdf.PageInches(next), wholePage: true);
            next++;
            e.HasMorePages = next < pdf.Count;
        };
        doc.Print();
        return new Result(true, printer, pdf.Count, "");
    }

    private static Result PrintPicture(string path, string printer, string jobName, string? toFile)
    {
        using var img = Image.FromFile(path);
        var inches = new SizeF(img.Width / Math.Max(1f, img.HorizontalResolution), img.Height / Math.Max(1f, img.VerticalResolution));
        using var doc = Document(printer, jobName, toFile);
        doc.QueryPageSettings += (_, e) => e.PageSettings.Landscape = img.Width > img.Height;
        doc.PrintPage += (_, e) => { DrawFitted(e, img, inches, wholePage: false); e.HasMorePages = false; };
        doc.Print();
        return new Result(true, printer, 1, "");
    }

    private static Result PrintText(string path, string printer, string jobName, string? toFile)
    {
        var lines = File.ReadAllLines(path);
        using var font = new Font("Consolas", 10f);
        using var doc = Document(printer, jobName, toFile);
        int line = 0, pages = 0;
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            float y = e.MarginBounds.Top, lh = font.GetHeight(g);
            var fmt = new StringFormat(StringFormatFlags.LineLimit);
            while (line < lines.Length)
            {
                var text = lines[line].Length == 0 ? " " : lines[line].Replace("\t", "    ");
                var size = g.MeasureString(text, font, e.MarginBounds.Width, fmt);
                if (y + size.Height > e.MarginBounds.Bottom && y > e.MarginBounds.Top) break;
                g.DrawString(text, font, Brushes.Black, new RectangleF(e.MarginBounds.Left, y, e.MarginBounds.Width, size.Height), fmt);
                y += Math.Max(lh, size.Height);
                line++;
            }
            pages++;
            e.HasMorePages = line < lines.Length;
        };
        doc.Print();
        return new Result(true, printer, pages, "");
    }

    /// <summary>
    /// The program Windows prints this kind of file with — Word for a .docx, and so on. "printto" names
    /// the printer; "print" only knows the default. Neither: said, never a silent nothing.
    /// </summary>
    private static Result PrintWithItsProgram(string path, string printer)
    {
        var verbs = new ProcessStartInfo(path).Verbs;
        bool printTo = verbs.Contains("printto", StringComparer.OrdinalIgnoreCase);
        bool print = verbs.Contains("print", StringComparer.OrdinalIgnoreCase);
        if (!printTo && !(print && printer == DefaultPrinter()))
            return new Result(false, printer, 0, $"nothing on this computer prints {Path.GetExtension(path)} files — send it as a PDF");
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true, Verb = printTo ? "printto" : "print",
            Arguments = printTo ? $"\"{printer}\"" : "", WindowStyle = ProcessWindowStyle.Hidden
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(60000);
        return new Result(true, printer, 0, "");
    }
}
