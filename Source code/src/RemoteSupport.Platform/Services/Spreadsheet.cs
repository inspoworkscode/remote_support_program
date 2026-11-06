using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Reads the first sheet of an .xlsx, or a .csv, into rows of text; and writes the one .xlsx the
/// product hands out, the client import template.
///
/// Ours rather than a package, like everything else: an .xlsx is a zip of XML, and the part of it an
/// import needs — shared strings, inline strings, numbers, and where each cell sits — is small. A
/// library would read formulas, dates and styles we have no use for.
///
/// <b>A cell remembers whether Excel stored it as a NUMBER.</b> That is the only thing about a cell
/// beyond its text worth keeping, because it is how a phone number loses its leading zero: type
/// 0823483295 into a General cell and Excel keeps 823483295. The text alone cannot tell that apart
/// from a number somebody meant; the cell type can. See <see cref="ClientImport"/>.
/// </summary>
public static class Spreadsheet
{
    public readonly record struct Cell(string Text, bool IsNumber)
    {
        public static readonly Cell Empty = new("", false);
    }

    /// <summary>Rows as they sit in the sheet; row 0 is the sheet's first row, blank rows included.</summary>
    public sealed class Sheet
    {
        public List<Cell[]> Rows { get; } = new();
    }

    /// <summary>Larger than any client list, and small enough that a wrong file is refused rather than chewed on.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Reads an upload by its content, not its name: an .xlsx starts "PK", anything else is taken as CSV.</summary>
    public static (Sheet? sheet, string error) Read(byte[] data, string fileName)
    {
        if (data.Length == 0) return (null, "The file is empty.");
        if (data.Length > MaxBytes) return (null, "That file is larger than 10 MB, which is far bigger than any client list.");
        bool zip = data.Length > 4 && data[0] == 'P' && data[1] == 'K';
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext == ".xls" && !zip) return (null, "That is the old Excel format (.xls). Open it in Excel and use Save As → Excel Workbook (.xlsx).");
        try
        {
            return zip ? (ReadXlsx(data, 0), "") : (ReadCsv(data), "");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or KeyNotFoundException or FormatException)
        {
            return (null, zip ? "That file could not be read as an Excel workbook." : "That file could not be read as a CSV.");
        }
    }

    // ---------------------------------------------------------------- xlsx

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// One sheet of an .xlsx by its place among the tabs (0 is the first) — for reading back a workbook of several
    /// sheets, as <c>tools/exportcheck</c> reads an export's. An import only ever reads the first.
    /// </summary>
    public static Sheet ReadXlsx(byte[] data, int index)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);

        XmlDocument Load(string path)
        {
            var e = zip.GetEntry(path) ?? throw new InvalidDataException("missing " + path);
            var doc = new XmlDocument { XmlResolver = null };
            using var s = e.Open();
            using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc.Load(r);
            return doc;
        }

        var ns = new XmlNamespaceManager(new NameTable());
        ns.AddNamespace("m", Main);
        ns.AddNamespace("r", RelNs);
        ns.AddNamespace("pr", "http://schemas.openxmlformats.org/package/2006/relationships");

        // The FIRST sheet as the workbook lists it — which is the tab on the left, not sheet1.xml,
        // once somebody has reordered or deleted tabs.
        var wb = Load("xl/workbook.xml");
        var first = wb.SelectNodes("//m:sheets/m:sheet", ns)?.Cast<XmlElement>().ElementAtOrDefault(index) ?? throw new InvalidDataException("no such sheet");
        var rid = first.GetAttribute("id", RelNs);
        var rels = Load("xl/_rels/workbook.xml.rels");
        var target = (rels.SelectSingleNode($"//pr:Relationship[@Id='{rid}']", ns) as XmlElement)?.GetAttribute("Target")
                     ?? "worksheets/sheet1.xml";
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
            foreach (XmlElement si in Load("xl/sharedStrings.xml").SelectNodes("//m:si", ns)!)
                shared.Add(TextOf(si, ns));

        var sheet = new Sheet();
        var ws = Load(sheetPath);
        foreach (XmlElement row in ws.SelectNodes("//m:sheetData/m:row", ns)!)
        {
            // Rows and cells may be skipped in the file when empty; their references say where they go.
            int r = int.TryParse(row.GetAttribute("r"), out var rn) ? rn - 1 : sheet.Rows.Count;
            while (sheet.Rows.Count < r) sheet.Rows.Add(Array.Empty<Cell>());
            var cells = new List<Cell>();
            foreach (XmlElement c in row.SelectNodes("m:c", ns)!)
            {
                var col = ColumnIndex(c.GetAttribute("r"), cells.Count);
                while (cells.Count < col) cells.Add(Cell.Empty);
                var t = c.GetAttribute("t");
                var v = (c.SelectSingleNode("m:v", ns) as XmlElement)?.InnerText ?? "";
                Cell cell = t switch
                {
                    "s" => new Cell(int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "", false),
                    "inlineStr" => new Cell(c.SelectSingleNode("m:is", ns) is XmlElement isn ? TextOf(isn, ns) : "", false),
                    "str" or "e" => new Cell(v, false),
                    "b" => new Cell(v == "1" ? "TRUE" : "FALSE", false),
                    _ => new Cell(NumberText(v), v.Length > 0),
                };
                if (cells.Count == col) cells.Add(cell); else cells[col] = cell;
            }
            sheet.Rows.Add(cells.ToArray());
        }
        return sheet;
    }

    /// <summary>Text of a shared or inline string: its plain &lt;t&gt;, or its rich-text runs joined, never its phonetic hints.</summary>
    private static string TextOf(XmlElement si, XmlNamespaceManager ns)
    {
        var sb = new StringBuilder();
        foreach (XmlElement t in si.SelectNodes("m:t | m:r/m:t", ns)!) sb.Append(t.InnerText);
        return sb.ToString();
    }

    /// <summary>"C12" → 2. A cell with no reference goes after the one before it.</summary>
    private static int ColumnIndex(string reference, int fallback)
    {
        int n = 0, i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++) n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? fallback : n - 1;
    }

    /// <summary>
    /// Excel stores 823483295 as "823483295" but a long one as "2.7824623001E10"; both should read
    /// as the digits somebody typed.
    /// </summary>
    private static string NumberText(string v)
    {
        if (v.Length == 0) return "";
        if (decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d == decimal.Truncate(d) ? decimal.Truncate(d).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                            : d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v;
    }

    // ---------------------------------------------------------------- csv

    /// <summary>
    /// CSV as Excel writes it: quoted fields, doubled quotes, newlines inside quotes. Comma unless the
    /// header row has more semicolons than commas — Excel in a comma-decimal locale writes semicolons.
    ///
    /// Encoding: UTF-8 when it decodes cleanly (with or without the BOM Excel's "CSV UTF-8" adds);
    /// otherwise Windows-1252, which is what Excel's plain "CSV" writes on a machine like Clint's.
    /// </summary>
    private static Sheet ReadCsv(byte[] data)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { text = Windows1252(data); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var firstLine = text.Split('\n', 2)[0];
        char sep = firstLine.Count(ch => ch == ';') > firstLine.Count(ch => ch == ',') ? ';' : ',';

        var sheet = new Sheet();
        var row = new List<Cell>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { row.Add(new Cell(field.ToString(), false)); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(new Cell(field.ToString(), false)); field.Clear();
                sheet.Rows.Add(row.ToArray()); row.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(new Cell(field.ToString(), false)); sheet.Rows.Add(row.ToArray()); }
        return sheet;
    }

    // ---------------------------------------------------------------- Windows-1252, both ways

    // The 32 characters at bytes 0x80–0x9F, where Windows-1252 differs from Latin-1. Everything else
    // in 0x00–0xFF is the same number either way. Written out rather than taken from a code-page
    // provider, because it is also needed BACKWARDS — see ClientImport.RepairMojibake.
    private static readonly char[] High1252 =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    public static string Windows1252(byte[] data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data) sb.Append(b is >= 0x80 and <= 0x9F ? High1252[b - 0x80] : (char)b);
        return sb.ToString();
    }

    /// <summary>The Windows-1252 byte for a character, or null when it has none.</summary>
    public static byte? To1252(char ch)
    {
        if (ch < 0x80 || ch is >= ' ' and <= 'ÿ') return (byte)ch;
        var i = Array.IndexOf(High1252, ch);
        return i >= 0 ? (byte)(0x80 + i) : null;
    }

    // ---------------------------------------------------------------- writing the template

    /// <summary>
    /// A workbook whose first sheet is a bold, frozen header row with the listed columns stored as
    /// TEXT, so a phone number typed into them keeps its leading zero. A second sheet says how to
    /// fill it in; the import reads only the first.
    /// </summary>
    public static byte[] WriteTemplate(IReadOnlyList<string> headers, IReadOnlyList<double> widths,
        ISet<int> textColumns, IReadOnlyList<string> readMe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            static string Col(int i) { var s = ""; for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s; return s; }
            static string Esc(string s) => SecurityElement.Escape(s) ?? "";

            Put("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");
            Put("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Put("xl/workbook.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{Main}\" xmlns:r=\"{RelNs}\">" +
                "<sheets><sheet name=\"Clients\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"How to fill this in\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");
            // Style 1: bold header. Style 2: number format 49, "@" — Text — so Excel keeps what is typed.
            Put("xl/styles.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"{Main}\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
                "</styleSheet>");

            var cols = new StringBuilder("<cols>");
            for (int i = 0; i < headers.Count; i++)
                cols.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i].ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customWidth=\"1\"{(textColumns.Contains(i) ? " style=\"2\"" : "")}/>");
            cols.Append("</cols>");
            var head = new StringBuilder("<row r=\"1\">");
            for (int i = 0; i < headers.Count; i++)
                head.Append($"<c r=\"{Col(i)}1\" s=\"1\" t=\"inlineStr\"><is><t>{Esc(headers[i])}</t></is></c>");
            head.Append("</row>");
            Put("xl/worksheets/sheet1.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{Main}\">" +
                "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
                $"{cols}<sheetData>{head}</sheetData></worksheet>");

            var help = new StringBuilder();
            for (int i = 0; i < readMe.Count; i++)
                help.Append($"<row r=\"{i + 1}\"><c r=\"A{i + 1}\"{(i == 0 ? " s=\"1\"" : "")} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(readMe[i])}</t></is></c></row>");
            Put("xl/worksheets/sheet2.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{Main}\">" +
                $"<cols><col min=\"1\" max=\"1\" width=\"120\" customWidth=\"1\"/></cols><sheetData>{help}</sheetData></worksheet>");
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A workbook of one or more sheets, written row by row as it goes — for a company's export (roadmap 4.9,
    /// 2026-10-22), where the story of four thousand tickets must not be held in memory first.
    ///
    /// <para><b>Every cell is TEXT</b> (inline strings, number format "@"), so a phone number keeps its leading zero
    /// and a ticket number is never turned into a date — the same rule as the import template. A cell longer than
    /// Excel takes (<see cref="MaxCell"/>) is cut, and says so; characters XML cannot carry are left out. A sheet past
    /// Excel's last row carries on in a sheet of the same name and "(2)".</para>
    /// </summary>
    public sealed class Workbook : IDisposable
    {
        /// <summary>The most characters Excel keeps in one cell.</summary>
        public const int MaxCell = 32767;
        private const int MaxRows = 1_048_576;

        private readonly ZipArchive _zip;
        private readonly List<string> _names = new();
        private StreamWriter? _w;
        private string _name = "";
        private IReadOnlyList<string> _headers = Array.Empty<string>();
        private IReadOnlyList<double> _widths = Array.Empty<double>();
        private int _row, _part;
        /// <summary>What a cut cell ends with — <paramref name="cutNote"/> says where the whole text is.</summary>
        private readonly string _cutNote;

        public Workbook(Stream to, string cutNote = "[cut here — longer than Excel holds in one cell]")
        {
            _zip = new ZipArchive(to, ZipArchiveMode.Create, leaveOpen: true);
            _cutNote = cutNote;
        }

        /// <summary>Starts the next sheet, with a bold, frozen header row. The one before it is finished.</summary>
        public void Sheet(string name, IReadOnlyList<string> headers, IReadOnlyList<double>? widths = null)
        {
            _name = name; _headers = headers; _widths = widths ?? headers.Select(_ => 24.0).ToList(); _part = 1;
            Begin(name);
        }

        private void Begin(string name)
        {
            End();
            // A tab's name: 31 characters, none of []:*?/\ — Excel refuses the file otherwise.
            var tab = new string(name.Select(c => "[]:*?/\\".Contains(c) ? ' ' : c).ToArray()).Trim();
            if (tab.Length > 31) tab = tab[..31];
            _names.Add(tab.Length > 0 ? tab : $"Sheet{_names.Count + 1}");
            _w = new StreamWriter(_zip.CreateEntry($"xl/worksheets/sheet{_names.Count}.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            _w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{Main}\">");
            _w.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
            for (int i = 0; i < _headers.Count; i++)
                _w.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{_widths[Math.Min(i, _widths.Count - 1)].ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customWidth=\"1\" style=\"2\"/>");
            _w.Write("</cols><sheetData>");
            _row = 0;
            Write(_headers, 1);
        }

        /// <summary>One row, in the header's order. Nulls are blank cells.</summary>
        public void Row(params string?[] cells) => Row((IReadOnlyList<string?>)cells);

        public void Row(IReadOnlyList<string?> cells)
        {
            if (_w == null) throw new InvalidOperationException("Start a sheet first.");
            if (_row >= MaxRows) Begin($"{(_name.Length > 26 ? _name[..26] : _name)} ({++_part})");
            Write(cells, 2);
        }

        private void Write(IReadOnlyList<string?> cells, int style)
        {
            _row++;
            _w!.Write($"<row r=\"{_row}\">");
            for (int i = 0; i < cells.Count; i++)
            {
                var text = Clean(cells[i]);
                if (text.Length == 0) continue;
                _w.Write($"<c r=\"{Col(i)}{_row}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                _w.Write(SecurityElement.Escape(text));
                _w.Write("</t></is></c>");
            }
            _w.Write("</row>");
        }

        /// <summary>The text as a cell can hold it: no character XML 1.0 forbids, and no longer than Excel keeps.</summary>
        private string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
                if (XmlConvert.IsXmlChar(c)) sb.Append(c);
            }
            if (sb.Length > MaxCell)
            {
                int keep = MaxCell - _cutNote.Length - 2;
                if (char.IsHighSurrogate(sb[keep - 1])) keep--;
                sb.Length = keep;
                sb.Append("\n\n").Append(_cutNote);
            }
            return sb.ToString();
        }

        private static string Col(int i) { var s = ""; for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s; return s; }

        private void End()
        {
            if (_w == null) return;
            _w.Write("</sheetData></worksheet>");
            _w.Dispose();
            _w = null;
        }

        public void Dispose()
        {
            End();
            if (_names.Count == 0) Sheet("Sheet1", Array.Empty<string>());
            End();
            void Put(string path, string xml)
            {
                using var w = new StreamWriter(_zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
            var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= _names.Count; i++)
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            Put("[Content_Types].xml", types.Append("</Types>").ToString());
            Put("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            var sheets = new StringBuilder();
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= _names.Count; i++)
            {
                sheets.Append($"<sheet name=\"{SecurityElement.Escape(_names[i - 1])}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
                rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            }
            rels.Append($"<Relationship Id=\"rId{_names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Put("xl/workbook.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{Main}\" xmlns:r=\"{RelNs}\"><sheets>{sheets}</sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", rels.ToString());
            // Style 1: bold header, text. Style 2: text, wrapped — a ticket's story has line breaks in it.
            Put("xl/styles.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"{Main}\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>" +
                "</styleSheet>");
            _zip.Dispose();
        }
    }
}
