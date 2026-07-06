using System.IO.Compression;
using System.Text;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Writes a tabular log to CSV or XLSX. XLSX is built by hand as an Open Packaging
/// ZIP (string cells as inline strings) so there's no spreadsheet-library dependency.
/// Returns the full path written.
/// </summary>
public static class LogExporter
{
    public enum Format { Csv, Xlsx }

    /// <summary>Picks a unique path in the user's Downloads folder for the given base name.</summary>
    public static string DownloadsPath(string baseName, Format format)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(dir, "Downloads");
        if (!Directory.Exists(downloads)) downloads = Path.GetTempPath();
        var ext = format == Format.Xlsx ? "xlsx" : "csv";
        var safe = string.Concat(baseName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (safe.Length > 80) safe = safe[..80];
        var path = Path.Combine(downloads, $"{safe}.{ext}");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(downloads, $"{safe} ({n++}).{ext}");
        return path;
    }

    /// <summary>A unique path in Downloads for an arbitrary extension (e.g. the verification report HTML).</summary>
    public static string DownloadsPath(string baseName, string ext)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(dir, "Downloads");
        if (!Directory.Exists(downloads)) downloads = Path.GetTempPath();
        var safe = string.Concat(baseName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (safe.Length > 80) safe = safe[..80];
        var path = Path.Combine(downloads, $"{safe}.{ext}");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(downloads, $"{safe} ({n++}).{ext}");
        return path;
    }

    public static void Write(Format format, string path, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        if (format == Format.Csv) WriteCsv(path, headers, rows);
        else WriteXlsx(path, headers, rows);
    }

    private static void WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(CsvCell)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r.Select(CsvCell)));
        // UTF-8 with BOM so Excel opens accented text correctly.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string CsvCell(string? v)
    {
        v ??= "";
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private static void WriteXlsx(string path, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
          + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
          + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
          + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
          + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
          + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
          + "</Types>");

        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
          + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
          + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
          + "</Relationships>");

        Add(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
          + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
          + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
          + "<sheets><sheet name=\"Log\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

        Add(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
          + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
          + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
          + "</Relationships>");

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        AppendRow(sheet, 1, headers.ToArray());
        var rowNum = 2;
        foreach (var r in rows) AppendRow(sheet, rowNum++, r);
        sheet.Append("</sheetData></worksheet>");
        Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    private static void AppendRow(StringBuilder sb, int rowNum, string[] cells)
    {
        sb.Append("<row r=\"").Append(rowNum).Append("\">");
        for (var c = 0; c < cells.Length; c++)
        {
            var refName = ColumnName(c) + rowNum;
            sb.Append("<c r=\"").Append(refName).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
              .Append(XmlEscape(cells[c] ?? "")).Append("</t></is></c>");
        }
        sb.Append("</row>");
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

    private static string XmlEscape(string v) => v
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void Add(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
