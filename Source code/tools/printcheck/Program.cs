using RemoteSupport.Host;
using RemoteSupport.Shared;
using System.Drawing;
using System.Drawing.Imaging;
using RemoteSupport.Media;

// ---------------------------------------------------------------------------------------------
// printcheck - remote printing, both directions (roadmap 1.8, 2026-10-06).
//
// Part 1, this PC, no elevation: the pieces both directions stand on, against Windows' own
// "Microsoft Print to PDF" printer told to write to a file — so nothing reaches paper and the result
// can be read back. Text, a picture and a PDF are printed; the PDFs Windows makes are opened with the
// PDF reader (PdfPages, Windows.Data.Pdf by hand) and their pages looked at.
//
//   dotnet run -c Debug --project tools/printcheck/printcheck.csproj
// ---------------------------------------------------------------------------------------------

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
}
void Log(string s) => Console.WriteLine("        " + s);

const string Pdf = "Microsoft Print to PDF";
var work = Path.Combine(Path.GetTempPath(), "rs-printcheck-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(work);
Console.WriteLine("printcheck — remote printing");
Console.WriteLine($"work folder {work}");
Console.WriteLine();

Check("this PC has \"Microsoft Print to PDF\"", DocumentPrinter.Printers().Contains(Pdf), string.Join(", ", DocumentPrinter.Printers()));

/// The spooler writes the file after Print() returns; wait for it to be there and finished.
bool Landed(string file, int ms = 20000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms); long last = -1;
    while (DateTime.UtcNow < until)
    {
        try
        {
            if (File.Exists(file))
            {
                using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                var len = new FileInfo(file).Length;
                if (len > 0 && len == last) return true;
                last = len;
            }
        }
        catch { }
        Thread.Sleep(300);
    }
    return false;
}
double Ink(Bitmap b)
{
    long dark = 0, all = 0;
    for (int y = 0; y < b.Height; y += 2) for (int x = 0; x < b.Width; x += 2) { var c = b.GetPixel(x, y); all++; if (c.R + c.G + c.B < 384) dark++; }
    return all == 0 ? 0 : (double)dark / all;
}

// ---- text ----------------------------------------------------------------------------------
Console.WriteLine("== text, three pages of it ==");
var txt = Path.Combine(work, "notes.txt");
File.WriteAllLines(txt, Enumerable.Range(1, 160).Select(i => $"Line {i:000}: the quick brown fox jumps over the lazy dog, invoice INV-{1000 + i}."));
var outTxt = Path.Combine(work, "notes.pdf");
var r = DocumentPrinter.Print(txt, Pdf, "printcheck notes", outTxt);
Check("printed", r.Ok, r.Error);
Check("...more than one page, and it says how many", r.Pages >= 2, $"{r.Pages} page(s)");
Check("the PDF Windows made has landed", Landed(outTxt), File.Exists(outTxt) ? $"{new FileInfo(outTxt).Length:N0} bytes" : "no file");
int textPages = 0;
try
{
    using var pdf = new PdfPages(outTxt);
    textPages = pdf.Count;
    Check("the PDF reader opens it and counts the same pages", pdf.Count == r.Pages, $"{pdf.Count} page(s)");
    var inches = pdf.PageInches(0);
    Log($"page 1 is {inches.Width:0.00} x {inches.Height:0.00} inches");
    Check("...a real sheet of paper (A4 or Letter)", Math.Abs(inches.Width - 8.27) < 0.1 && Math.Abs(inches.Height - 11.69) < 0.1
        || Math.Abs(inches.Width - 8.5) < 0.1 && Math.Abs(inches.Height - 11) < 0.1, $"{inches.Width:0.00} x {inches.Height:0.00}");
    using var page = pdf.Render(0, 100);
    page.Save(Path.Combine(work, "notes-page1.png"), ImageFormat.Png);
    using var page2 = pdf.Render(1, 100);
    Check("page 2 draws at the size asked for, the display zoom taken out", Math.Abs(page2.Width - inches.Width * 100) <= 3 && Math.Abs(page2.Height - inches.Height * 100) <= 3, $"{page2.Width}x{page2.Height}");
    Check("...and both pages say their real size in inches", Math.Abs(page.Width / page.HorizontalResolution - inches.Width) < 0.02 && Math.Abs(page2.Height / page2.VerticalResolution - inches.Height) < 0.02);
    var ink = Ink(page);
    Check("...and has writing on it", ink > 0.005 && ink < 0.5, $"{ink:P2} dark");
}
catch (Exception ex) { Check("the PDF reader opens it", false, ex.Message); }

// ---- a PDF printed again: what the technician's console does with a customer's job ----------
Console.WriteLine("== a PDF, printed again (the console printing a customer's job) ==");
var outAgain = Path.Combine(work, "notes-again.pdf");
r = DocumentPrinter.Print(outTxt, Pdf, "printcheck again", outAgain);
Check("printed", r.Ok, r.Error);
Check("...every page", r.Pages == textPages, $"{r.Pages} of {textPages}");
Check("landed", Landed(outAgain));
try
{
    using var a = new PdfPages(outTxt);
    using var b2 = new PdfPages(outAgain);
    Check("same page count", a.Count == b2.Count, $"{a.Count} / {b2.Count}");
    using var pa = a.Render(0, 60); using var pb = b2.Render(0, 60);
    double ia = Ink(pa), ib = Ink(pb);
    Check("page 1 carries about the same ink — the same page, not a blank one", ib > 0.004 && Math.Abs(ia - ib) < Math.Max(0.02, ia * 0.6), $"{ia:P2} then {ib:P2}");
    pb.Save(Path.Combine(work, "notes-again-page1.png"), ImageFormat.Png);
}
catch (Exception ex) { Check("the reprint opens", false, ex.Message); }

// ---- a picture, landscape ----------------------------------------------------------------
Console.WriteLine("== a picture ==");
var png = Path.Combine(work, "chart.png");
using (var bmp = new Bitmap(1600, 900))
using (var g = Graphics.FromImage(bmp))
{
    g.Clear(Color.White);
    for (int i = 0; i < 8; i++) g.FillRectangle(Brushes.DarkBlue, 100 + i * 180, 800 - i * 90, 120, i * 90 + 50);
    bmp.SetResolution(150, 150);
    bmp.Save(png, ImageFormat.Png);
}
var outPng = Path.Combine(work, "chart.pdf");
r = DocumentPrinter.Print(png, Pdf, "printcheck chart", outPng);
Check("printed, one page", r.Ok && r.Pages == 1, r.Error);
Check("landed", Landed(outPng));
try
{
    using var p = new PdfPages(outPng);
    var s = p.PageInches(0);
    Check("a wide picture goes on a landscape page", s.Width > s.Height, $"{s.Width:0.00} x {s.Height:0.00}");
    using var pg = p.Render(0, 60);
    Check("...with the picture on it", Ink(pg) > 0.03, $"{Ink(pg):P2} dark");
}
catch (Exception ex) { Check("opens", false, ex.Message); }

// ---- refusals ------------------------------------------------------------------------------
Console.WriteLine("== refusals ==");
var odd = Path.Combine(work, "thing.xyz123");
File.WriteAllText(odd, "?");
r = DocumentPrinter.Print(odd, Pdf, "printcheck odd", Path.Combine(work, "odd.pdf"));
Check("a kind of file nothing here prints is refused, saying what to do", !r.Ok && r.Error.Contains("send it as a PDF"), r.Error);
r = DocumentPrinter.Print(txt, "No Such Printer 123", "x", Path.Combine(work, "x.pdf"));
Check("a printer that does not exist is refused by name", !r.Ok && r.Error.Contains("No Such Printer 123"), r.Error);
bool threw = false; try { using var bad = new PdfPages(txt); } catch (Exception ex) { threw = true; Log("a text file as a PDF: " + ex.Message); }
Check("a file that is not a PDF is refused by the reader, not drawn as nothing", threw);

// ---- the customer's side, without a spooler change ---------------------------------------
Console.WriteLine("== the customer's side: names, paths and the helper ==");
Check("the printer is named for the technician and the product", PrintRedirect.PrinterName("Sue Tech", "Coastal IT Remote Support") == "Sue Tech's printer (Coastal IT Remote Support)");
Check("...with nothing a printer name may not hold (comma, backslash)", !PrintRedirect.PrinterName("Smith, J\\x", "A,B").Any(c => c is ',' or '\\'), PrintRedirect.PrinterName("Smith, J\\x", "A,B"));
Check("...and never longer than 120 characters", PrintRedirect.PrinterName(new string('x', 300), "p").Length <= 120);
var inRoot = Path.Combine(work, "in");
var staged = Path.Combine(inRoot, "120000-abc123", "letter.pdf");
Directory.CreateDirectory(Path.GetDirectoryName(staged)!); File.WriteAllText(staged, "x");
var outside = Path.Combine(work, "secret.txt"); File.WriteAllText(outside, "x");
Directory.CreateDirectory(inRoot + "-evil"); File.WriteAllText(Path.Combine(inRoot + "-evil", "a.pdf"), "x");
Check("the host prints a file waiting in the print folder", HostPaths.IsPrintStaging(staged, inRoot));
Check("...and NOT one elsewhere on the machine", !HostPaths.IsPrintStaging(outside, inRoot));
Check("...nor one climbing out with \"..\"", !HostPaths.IsPrintStaging(Path.Combine(inRoot, "120000-abc123", "..", "..", "secret.txt"), inRoot));
Check("...nor one in a folder whose name merely starts the same", !HostPaths.IsPrintStaging(Path.Combine(inRoot + "-evil", "a.pdf"), inRoot));
var outRoot = Path.Combine(work, "out");
Check("a finished job is recognised so its pull is not also written as a copy", PrintRedirect.IsJobFile(Path.Combine(outRoot, "job-1.pdf"), outRoot)
    && !PrintRedirect.IsJobFile(Path.Combine(outRoot, "tech-1.pdf"), outRoot) && !PrintRedirect.IsJobFile(Path.Combine(work, "job-1.pdf"), outRoot));

// The helper, as it is run on a customer's machine — with a file it must refuse, so nothing is printed here.
var hostExe = Path.Combine(FindRepoRoot(), "src", "RemoteSupport.Host", "bin", "Debug", "net10.0-windows", "RemoteSupport.Host.exe");
if (File.Exists(hostExe))
{
    var answer = Path.Combine(work, "answer.json");
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(hostExe, $"--print-file \"{odd}\" \"{answer}\"") { UseShellExecute = false, CreateNoWindow = true })!;
    p.WaitForExit(60000);
    var said = File.Exists(answer) ? File.ReadAllText(answer) : "";
    Check("the helper answers in writing, with the reason, and fails", p.ExitCode == 1 && said.Contains("send it as a PDF"), $"exit {p.ExitCode}: {said}");
}
else Check("the host is built (for the helper)", false, hostExe);

// The activity lines, as the ticket and the tray app say them.
var outLine = new FileActivityMsg { Kind = "print-out", FromMachine = true, Label = "Invoice-1042" }.Describe("Sue Tech");
var inLine = new FileActivityMsg { Kind = "print-in", Label = "letter.pdf", Printer = "HP LaserJet", FileCount = 1 }.Describe("Sue Tech");
Log(outLine); Log(inLine);
Check("a print to the technician reads as one", outLine == "“Invoice-1042” was printed on this computer to Sue Tech's printer.");
Check("a print on their printer says which, and the pages, in the singular for one", inLine == "Sue Tech printed “letter.pdf” on this computer's printer, HP LaserJet (1 page).");

// ---- the spooler: needs an elevated prompt ------------------------------------------------
if (args.Contains("--elevated"))
{
    Console.WriteLine("== the spooler (elevated): a technician's printer on this machine ==");
    var techOut = Path.Combine(work, "spool");
    int left = PrintRedirect.SweepLeftovers(techOut);
    using var pr = new PrintRedirect(techOut);
    var ready = new System.Collections.Concurrent.ConcurrentQueue<(string Tech, string Path, string Doc)>();
    int lost = 0;
    pr.JobReady += (t, path, d) => ready.Enqueue((t, path, d));
    pr.JobsLost += (_, n) => Interlocked.Add(ref lost, n);
    var name = pr.Add("Printcheck Tech", "printcheck", out var err);
    Check("the printer is added", name != null, err);
    var listed = Spooler.Printers().FirstOrDefault(x => x.Name == name);
    Check("...Windows lists it, on a file port in the print folder", listed.Port.StartsWith(techOut, StringComparison.OrdinalIgnoreCase), listed.Port);
    Check("...and it can be printed to by name", DocumentPrinter.Printers().Contains(name ?? "?"));

    r = DocumentPrinter.Print(txt, name, "Quarterly report");
    Check("a document printed to it is accepted by the spooler", r.Ok, r.Error);
    var until = DateTime.UtcNow.AddSeconds(30);
    while (ready.IsEmpty && DateTime.UtcNow < until) Thread.Sleep(200);
    Check("the host picks the finished PDF up", ready.TryDequeue(out var job), "");
    Check("...for the right technician, named as the program named it", job.Tech == "Printcheck Tech" && job.Doc == "Quarterly report", $"{job.Tech} / {job.Doc}");
    try
    {
        using var got = new PdfPages(job.Path);
        Check("...and it is the whole document", got.Count == r.Pages, $"{got.Count} of {r.Pages} pages");
    }
    catch (Exception ex) { Check("...and it opens as a PDF", false, ex.Message); }

    // Two straight after each other: both arrive, or the one lost is SAID.
    DocumentPrinter.Print(png, name, "Chart one");
    DocumentPrinter.Print(png, name, "Chart two");
    until = DateTime.UtcNow.AddSeconds(30);
    while (ready.Count + lost < 2 && DateTime.UtcNow < until) Thread.Sleep(200);
    Log($"two quick jobs: {ready.Count} arrived, {lost} said lost");
    Check("two quick jobs: each arrives or is said to be lost — none vanishes quietly", ready.Count + lost >= 2, $"{ready.Count} + {lost}");

    pr.Remove("Printcheck Tech");
    Thread.Sleep(500);
    Check("switched off, the printer is gone", !Spooler.Printers().Any(x => x.Name == name));

    // A host that died: its printer is swept by the next one.
    // A host killed mid-session never runs Remove: this one is simply never disposed.
    var crashed = new PrintRedirect(techOut);
    crashed.Add("Crashed Tech", "printcheck", out _);
    Check("a printer left by a host that died is still there…", Spooler.Printers().Any(x => x.Name == PrintRedirect.PrinterName("Crashed Tech", "printcheck")));
    Check("...until the next host sweeps it", PrintRedirect.SweepLeftovers(techOut) == 1 && !Spooler.Printers().Any(x => x.Name == PrintRedirect.PrinterName("Crashed Tech", "printcheck")));
}
else Log("the spooler part needs an elevated prompt: ... -- --elevated");

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Host"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}
