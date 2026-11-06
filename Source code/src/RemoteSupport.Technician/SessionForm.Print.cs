using RemoteSupport.Media;
using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// Remote printing in the session window (roadmap 1.8, 2026-10-06) — Clint chose both directions.
///
/// <list type="bullet">
/// <item><b>Print on my printer</b> (a tick in Tools): their computer gets a printer named for this
/// technician; what they print on it comes out on the printer chosen here. Each job arrives as a PDF,
/// pulled with the ordinary single-file pull, and is printed by <see cref="DocumentPrinter"/>.</item>
/// <item><b>Print a file on their printer…</b>: a file of the technician's, sent with the ordinary
/// single-file send to their print folder, then printed on their default printer by a helper running as
/// them. The answer — which printer, how many pages, or why not — comes back to the status bar.</item>
/// </list>
///
/// Both go on the ticket and into the person's tray app from the host's side (FileActivityMsg). Every
/// file id used here starts "print", so a print pulled for one technician is never saved by another
/// technician's window in the same session.
/// </summary>
public sealed partial class SessionForm
{
    /// <summary>Who this window is signed in as — the printer on their computer is named for them.
    /// Set by the dashboard; empty (the guest console) means no printing.</summary>
    public string Technician { get; set; } = "";

    private ToolStripMenuItem _printHereItem = null!, _printThereItem = null!;
    private bool _printOn;
    private string _myPrinter = "";
    /// <summary>Print jobs being pulled for this window: file id -> the document's name.</summary>
    private readonly Dictionary<string, string> _printPulls = new();
    /// <summary>Files on their way to be printed on their printer: file id -> the file's name.</summary>
    private readonly Dictionary<string, string> _printSends = new();
    /// <summary>Print requests this window made, so only it reports how they went.</summary>
    private readonly HashSet<string> _printAsked = new();
    private string PrintTemp => Path.Combine(Path.GetTempPath(), "RemoteSupport", "print");

    private void BuildPrintItems()
    {
        _printHereItem = new ToolStripMenuItem("Print on my printer") { Font = _t.Body };
        _printHereItem.ToolTipText = "Their computer gets a printer named for you; what they print on it comes out here";
        _printHereItem.Click += (_, _) => TogglePrintHere();
        _printThereItem = new ToolStripMenuItem("Print a file on their printer…") { Font = _t.Body };
        _printThereItem.ToolTipText = "Pick a file here; it is printed on their default printer";
        _printThereItem.Click += (_, _) => PrintFileThere();
        _toolsMenu.Items.Add(_printHereItem);
        _toolsMenu.Items.Add(_printThereItem);
        _toolsMenu.Opening += (_, _) => _printHereItem.Enabled = _printThereItem.Enabled = Technician.Length > 0;
    }

    private void TogglePrintHere()
    {
        if (Technician.Length == 0) return;
        if (_printOn)
        {
            _link.Send(new RemotePrintMsg { Technician = Technician, On = false });
            SetTransferStatus("Taking your printer off their computer…");
            return;
        }
        if (DocumentPrinter.Printers().Count == 0)
        {
            MessageBox.Show(this, "This computer has no printer set up.", "Print on my printer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Which of MY printers. Windows' own Print window: the one every technician already knows.
        using var dlg = new PrintDialog { UseEXDialog = true, AllowSomePages = false, AllowPrintToFile = false, AllowSelection = false };
        if (_myPrinter.Length > 0) dlg.PrinterSettings.PrinterName = _myPrinter;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _myPrinter = dlg.PrinterSettings.PrinterName;
        _link.Send(new RemotePrintMsg { Technician = Technician, On = true });
        SetTransferStatus($"Adding a printer for you to {_session.MachineName}…");
    }

    private void PrintFileThere()
    {
        if (Technician.Length == 0) return;
        using var ofd = new OpenFileDialog
        {
            Title = $"Print a file on {_session.MachineName}'s printer",
            Filter = "Documents (PDF, pictures, text, Office)|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.txt;*.log;*.csv;*.doc;*.docx;*.xls;*.xlsx;*.rtf|All files|*.*"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var fileId = "printin-" + Guid.NewGuid().ToString("N");
        var fi = new FileInfo(ofd.FileName);
        _printSends[fileId] = fi.Name;
        SetTransferStatus($"Sending {fi.Name} to print on {_session.MachineName}…");
        _ = Task.Run(async () =>
        {
            try
            {
                _link.Send(new FileOfferMsg { FileId = fileId, Name = fi.Name, Size = fi.Length, TargetDir = RemotePaths.PrintStaging });
                await using var fs = File.OpenRead(fi.FullName);
                var buf = new byte[64 * 1024]; long off = 0; int r;
                while ((r = await fs.ReadAsync(buf)) > 0)
                {
                    if (!await FileSetSender.SendChunkAsync(b => _link.SendBinary(b), Bin.FileChunk(fileId, off, buf.AsSpan(0, r)), CancellationToken.None))
                        throw new IOException("the link would not carry the whole file");
                    off += r; await Task.Delay(1);
                }
            }
            catch (Exception ex) { BeginInvoke(() => { _printSends.Remove(fileId); SetTransferStatus($"Could not send {fi.Name} to print: {ex.Message}"); }); }
        });
    }

    /// <summary>The print messages, and the file messages that belong to printing. True when handled here.</summary>
    private bool HandlePrintMsg(Msg m)
    {
        switch (m)
        {
            case RemotePrintStateMsg st when st.Technician == Technician:
                _printOn = st.On;
                _printHereItem.Checked = st.On;
                if (st.Error.Length > 0) SetTransferStatus($"Could not add your printer to {_session.MachineName}: {st.Error}");
                else if (st.Lost > 0) SetTransferStatus($"{st.Lost} print job{(st.Lost == 1 ? " was" : "s were")} printed too close together and did not arrive — ask them to print {(st.Lost == 1 ? "it" : "them")} again.");
                else if (st.On) SetTransferStatus($"{_session.MachineName} has a printer called “{st.Printer}” — what they print on it comes out on {_myPrinter}.");
                else SetTransferStatus($"Your printer is off {_session.MachineName}.");
                return true;
            case RemotePrintStateMsg:
                return true;   // another technician's printer

            case RemotePrintJobMsg job when job.Technician == Technician:
            {
                if (!_printOn) return true;   // switched off while it was on its way
                var fileId = "print-" + job.JobId;
                _printPulls[fileId] = job.Document;
                _link.Send(new FilePullMsg { Path = job.Path, FileId = fileId });
                SetTransferStatus($"Receiving “{job.Document}” to print…");
                return true;
            }
            case RemotePrintJobMsg:
                return true;   // another technician's

            case FileOfferMsg fo when fo.FileId.StartsWith("print-", StringComparison.Ordinal):
                // A print pulled by THIS window lands in the print folder, not Downloads; anybody
                // else's is not this window's business, and its chunks then find no file to go into.
                if (_printPulls.ContainsKey(fo.FileId))
                {
                    try
                    {
                        Directory.CreateDirectory(PrintTemp);
                        var path = Path.Combine(PrintTemp, fo.FileId + ".pdf");
                        _incoming[fo.FileId] = new FileStream(path, FileMode.Create, FileAccess.Write);
                        _paths[fo.FileId] = path;
                    }
                    catch (Exception ex) { _printPulls.Remove(fo.FileId); SetTransferStatus("Could not receive a print: " + ex.Message); }
                }
                return true;

            case FileDoneMsg fd when _printPulls.TryGetValue(fd.FileId, out var document):
            {
                _printPulls.Remove(fd.FileId);
                var path = _paths.TryGetValue(fd.FileId, out var p) ? p : "";
                if (_incoming.Remove(fd.FileId, out var fs)) { try { fs.Flush(); fs.Dispose(); } catch { } }
                _paths.Remove(fd.FileId);
                if (!fd.Ok) { SetTransferStatus($"“{document}” did not arrive: {fd.Error}"); TryDelete(path); return true; }
                var printer = _myPrinter;
                SetTransferStatus($"Printing “{document}” on {printer}…");
                _ = Task.Run(() =>
                {
                    var r = DocumentPrinter.Print(path, printer, document);
                    TryDelete(path);
                    BeginInvoke(() => SetTransferStatus(r.Ok
                        ? $"Printed “{document}” on {r.Printer} ({r.Pages} page{(r.Pages == 1 ? "" : "s")})."
                        : $"Could not print “{document}”: {r.Error}"));
                });
                return true;
            }
            case FileOfferMsg fo2 when fo2.FileId.StartsWith("printin-", StringComparison.Ordinal):
                return true;   // never offered TO a console; ignored if it were
            case FileDoneMsg fd2 when _printSends.TryGetValue(fd2.FileId, out var name):
            {
                _printSends.Remove(fd2.FileId);
                if (!fd2.Ok || string.IsNullOrEmpty(fd2.SavedPath)) { SetTransferStatus($"Could not send {name} to print: {fd2.Error}"); return true; }
                _printAsked.Add(fd2.FileId);
                _link.Send(new PrintFileMsg { JobId = fd2.FileId, Technician = Technician, Path = fd2.SavedPath, Name = name });
                SetTransferStatus($"Printing {name} on {_session.MachineName}…");
                return true;
            }
            case PrintFileResultMsg pr when !_printAsked.Remove(pr.JobId):
                return true;   // another technician's
            case PrintFileResultMsg pr:
                SetTransferStatus(pr.Ok
                    ? $"Printed on {_session.MachineName}'s printer, {pr.Printer}{(pr.Pages > 0 ? $" ({pr.Pages} page{(pr.Pages == 1 ? "" : "s")})" : "")}."
                    : $"Could not print on {_session.MachineName}: {pr.Error}");
                return true;
        }
        return false;
    }

    private static void TryDelete(string path) { try { if (path.Length > 0) File.Delete(path); } catch { } }
}
