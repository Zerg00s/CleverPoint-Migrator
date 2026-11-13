using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>What a column of a sheet holds, which decides how its cells are written.</summary>
public enum XlsxCell { Text, Number, Date }

/// <summary>
/// Writes an Excel workbook with any number of sheets, one row at a time, without a spreadsheet
/// library and without holding the data in memory. Each sheet gets a bold frozen header, a filter on
/// it, column widths that fit the first rows, real numbers and real dates.
///
/// Sheets are written one after the other: finish (dispose) a sheet before adding the next.
/// </summary>
public sealed class XlsxWriter : IDisposable
{
    /// <summary>Rows Excel can hold in a sheet, not counting the header.</summary>
    public const int MaxDataRows = 1_048_575;

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly List<string> _sheets = new();
    private Sheet? _open;

    public XlsxWriter(string path)
    {
        _file = File.Create(path);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create);
    }

    /// <summary>Starts a sheet. Dispose it when its rows are written.</summary>
    public Sheet AddSheet(string name, IReadOnlyList<(string Header, XlsxCell Type)> columns)
    {
        if (_open is { Closed: false }) throw new InvalidOperationException("Finish the previous sheet first.");
        _sheets.Add(UniqueName(name));
        var entry = _zip.CreateEntry($"xl/worksheets/sheet{_sheets.Count}.xml", CompressionLevel.Optimal);
        _open = new Sheet(new StreamWriter(entry.Open(), new UTF8Encoding(false), 1 << 16), columns);
        return _open;
    }

    private string UniqueName(string name)
    {
        // Excel: at most 31 characters, none of []:*?/\ and no two sheets alike.
        var clean = string.Concat(name.Select(c => "[]:*?/\\".Contains(c) ? ' ' : c)).Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        var candidate = clean;
        for (var n = 2; _sheets.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
        {
            var suffix = $" ({n})";
            candidate = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return candidate;
    }

    public void Dispose()
    {
        _open?.Dispose();
        if (_sheets.Count == 0) AddSheet("Sheet1", new[] { ("", XlsxCell.Text) }).Dispose();

        var types = new StringBuilder();
        types.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>")
            .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= _sheets.Count; i++)
            types.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        types.Append("</Types>");
        Add("[Content_Types].xml", types.ToString());

        Add("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
            + "</Relationships>");

        var workbook = new StringBuilder();
        workbook.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append($"<workbook xmlns=\"{Main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var i = 1; i <= _sheets.Count; i++)
            workbook.Append($"<sheet name=\"{Escape(_sheets[i - 1])}\" sheetId=\"{i}\" r:id=\"rId{i}\"/>");
        workbook.Append("</sheets></workbook>");
        Add("xl/workbook.xml", workbook.ToString());

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= _sheets.Count; i++)
            rels.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        rels.Append($"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>")
            .Append("</Relationships>");
        Add("xl/_rels/workbook.xml.rels", rels.ToString());

        // Cell styles: 0 = plain, 1 = bold (header), 2 = date and time.
        Add("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + $"<styleSheet xmlns=\"{Main}\">"
            + "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm\"/></numFmts>"
            + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"3\">"
            + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
            + "</cellXfs></styleSheet>");

        _zip.Dispose();
        _file.Dispose();
    }

    private void Add(string name, string content)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static string Escape(string value)
    {
        // Control characters are not allowed in XML 1.0 at all, escaped or not.
        StringBuilder? cleaned = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var bad = c < 0x20 && c is not ('\t' or '\n' or '\r') || c is '￾' or '￿'
                      || char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                         && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]));
            if (bad || c is '&' or '<' or '>' or '"')
            {
                cleaned ??= new StringBuilder(value, 0, i, value.Length + 16);
                cleaned.Append(bad ? "" : c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", _ => "&quot;" });
            }
            else cleaned?.Append(c);
        }
        return cleaned?.ToString() ?? value;
    }

    /// <summary>One sheet being written.</summary>
    public sealed class Sheet : IDisposable
    {
        /// <summary>Rows looked at to decide how wide the columns are.</summary>
        private const int Sample = 200;
        /// <summary>The longest text Excel accepts in a cell.</summary>
        private const int MaxCellText = 32767;

        private readonly StreamWriter _writer;
        private readonly IReadOnlyList<(string Header, XlsxCell Type)> _columns;
        private readonly List<object?[]> _sample = new();
        private bool _started;
        private int _rows;
        internal bool Closed { get; private set; }

        /// <summary>Data rows written so far.</summary>
        public int Rows => _rows + _sample.Count;
        /// <summary>The sheet is full; further rows are dropped.</summary>
        public bool Full => Rows >= MaxDataRows;

        internal Sheet(StreamWriter writer, IReadOnlyList<(string Header, XlsxCell Type)> columns)
        {
            _writer = writer;
            _columns = columns;
        }

        /// <summary>Adds a row. Returns false, and writes nothing, once the sheet is full.</summary>
        public bool WriteRow(IReadOnlyList<object?> cells)
        {
            if (Full) return false;
            var row = new object?[_columns.Count];
            for (var i = 0; i < row.Length && i < cells.Count; i++) row[i] = cells[i];
            if (!_started)
            {
                _sample.Add(row);
                if (_sample.Count >= Sample) Start();
            }
            else
            {
                Write(row, ++_rows + 1);
            }
            return true;
        }

        private void Start()
        {
            _started = true;
            _writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            _writer.Write($"<worksheet xmlns=\"{Main}\">");
            _writer.Write("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            _writer.Write("<cols>");
            for (var c = 0; c < _columns.Count; c++)
            {
                var widest = _columns[c].Header.Length + 3;       // room for the filter button
                foreach (var row in _sample) widest = Math.Max(widest, Display(row[c], _columns[c].Type).Length);
                var width = Math.Clamp(widest + 2, 8, 70);
                _writer.Write($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width}\" customWidth=\"1\"/>");
            }
            _writer.Write("</cols><sheetData><row r=\"1\">");
            foreach (var (header, _) in _columns)
                _writer.Write($"<c t=\"inlineStr\" s=\"1\"><is><t xml:space=\"preserve\">{Escape(header)}</t></is></c>");
            _writer.Write("</row>");
            foreach (var row in _sample) Write(row, ++_rows + 1);
            _sample.Clear();
        }

        private static string Display(object? value, XlsxCell type) => value switch
        {
            null => "",
            DateTime => "0000-00-00 00:00",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };

        private void Write(object?[] row, int number)
        {
            _writer.Write("<row r=\"");
            _writer.Write(number);
            _writer.Write("\">");
            for (var c = 0; c < row.Length; c++)
            {
                var value = row[c];
                switch (value)
                {
                    case null:
                    case string { Length: 0 }:
                        _writer.Write("<c/>");
                        break;
                    case DateTime date:
                        _writer.Write("<c s=\"2\"><v>");
                        _writer.Write(date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                        _writer.Write("</v></c>");
                        break;
                    case bool flag:
                        _writer.Write(flag ? "<c t=\"inlineStr\"><is><t>Yes</t></is></c>" : "<c/>");
                        break;
                    case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                        when _columns[c].Type == XlsxCell.Number:
                        _writer.Write("<c><v>");
                        _writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture));
                        _writer.Write("</v></c>");
                        break;
                    default:
                        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                        if (text.Length > MaxCellText) text = text[..MaxCellText];
                        _writer.Write("<c t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                        _writer.Write(Escape(text));
                        _writer.Write("</t></is></c>");
                        break;
                }
            }
            _writer.Write("</row>");
        }

        public void Dispose()
        {
            if (Closed) return;
            if (!_started) Start();
            _writer.Write("</sheetData>");
            if (_columns.Count > 0)
                _writer.Write($"<autoFilter ref=\"A1:{ColumnName(_columns.Count - 1)}{_rows + 1}\"/>");
            _writer.Write("</worksheet>");
            _writer.Dispose();
            Closed = true;
        }

        private static string ColumnName(int index)
        {
            var name = "";
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                name = (char)('A' + rem) + name;
                index = (index - 1) / 26;
            }
            return name;
        }
    }
}
