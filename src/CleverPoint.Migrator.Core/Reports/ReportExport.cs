using System.Globalization;
using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>Turns a report run into files for people: CSV per table, or one Excel workbook.</summary>
public static class ReportExport
{
    private static readonly ReportColumn[] SiteColumns =
    {
        new("site"), new("targetSite"), new("status"), new("rows", ColumnType.Number), new("seconds", ColumnType.Number),
        new("finished", ColumnType.Date), new("error"),
    };

    /// <summary>
    /// Writes the run as CSV: one file per table, plus one with how each site ended when there is more
    /// than one site. With a single file to write, <paramref name="path"/> is that file; otherwise the
    /// table name is added before the extension.
    /// </summary>
    public static List<string> ExportCsv(ReportRun run, ReportDefinition report, ReportStore store, string path)
    {
        var sites = store.Sites();
        var withSites = report.Scope != ReportScope.Tenant;
        var single = report.Tables.Count == 1 && !withSites;
        var written = new List<string>();
        string FileFor(string part) => single ? path
            : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}-{part}{Path.GetExtension(path)}");

        foreach (var table in report.Tables)
        {
            var columns = ReportCatalog.Columns(report, table, store.UsedColumns(table.Name));
            var file = FileFor(table.Name);
            using var writer = new StreamWriter(file, append: false, new UTF8Encoding(true));   // BOM: Excel reads accented text right
            writer.WriteLine(string.Join(",", columns.Select(c => CsvCell(ReportCatalog.Heading(c.Name), isText: false))));
            foreach (var row in store.ReadRows(table.Name))
                writer.WriteLine(string.Join(",", columns.Select(c => CsvCell(Text(row, c), c.Type == ColumnType.Text))));
            written.Add(file);
        }
        if (withSites)
        {
            var file = FileFor("sites");
            using var writer = new StreamWriter(file, append: false, new UTF8Encoding(true));
            var columns = SiteColumns.Where(c => c.Name != "targetSite" || report.Scope == ReportScope.SitePairs).ToList();
            writer.WriteLine(string.Join(",", columns.Select(c => CsvCell(ReportCatalog.Heading(c.Name), isText: false))));
            foreach (var site in sites)
                writer.WriteLine(string.Join(",", columns.Select(c => CsvCell(SiteText(site, c.Name), c.Type == ColumnType.Text))));
            written.Add(file);
        }
        return written;
    }

    /// <summary>
    /// Writes the run as one Excel workbook: a summary sheet, a sheet with how each site ended, and a
    /// sheet per table. Returns notes for the caller, for example that a table is larger than a sheet.
    /// </summary>
    public static List<string> ExportXlsx(ReportRun run, ReportDefinition report, ReportStore store, string path)
    {
        var notes = new List<string>();
        var sites = store.Sites();
        var counts = store.RowCounts();
        var tooLarge = report.Tables.Where(t => counts.GetValueOrDefault(t.Name) > XlsxWriter.MaxDataRows).ToList();
        foreach (var table in tooLarge)
            notes.Add($"Table '{table.Name}' has {counts[table.Name]:N0} rows; an Excel sheet holds {XlsxWriter.MaxDataRows:N0}. The sheet has the first {XlsxWriter.MaxDataRows:N0}. Export as CSV for all of them.");

        using var workbook = new XlsxWriter(path);

        using (var summary = workbook.AddSheet("Summary", new[] { ("Item", XlsxCell.Text), ("Value", XlsxCell.Number) }))
        {
            void Line(string item, object? value) => summary.WriteRow(new[] { item, value });
            Line("Report", report.Title);
            Line("Name", run.Name);
            Line("Run", run.Id);
            Line("Status", StatusText(run.Status));
            Line("Started (UTC)", run.StartedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            Line("Finished (UTC)", run.FinishedUtc?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (report.Scope != ReportScope.Tenant)
            {
                Line("Sites", sites.Count);
                Line("Sites scanned", sites.Count(s => s.Ok));
                Line("Sites not scanned", sites.Count(s => !s.Ok));
            }
            foreach (var table in report.Tables) Line($"Rows: {table.Title}", counts.GetValueOrDefault(table.Name));
            foreach (var (name, value) in store.Summary()) Line(ReportCatalog.Heading(name), value);
            foreach (var note in notes) Line("Note", note);
        }

        if (report.Scope != ReportScope.Tenant)
        {
            var columns = SiteColumns.Where(c => c.Name != "targetSite" || report.Scope == ReportScope.SitePairs).ToList();
            using var sheet = workbook.AddSheet("Sites", columns.Select(c => (ReportCatalog.Heading(c.Name), CellType(c.Type))).ToList());
            foreach (var site in sites) sheet.WriteRow(columns.Select(c => SiteCell(site, c.Name)).ToList());
        }

        foreach (var table in report.Tables)
        {
            var columns = ReportCatalog.Columns(report, table, store.UsedColumns(table.Name));
            using var sheet = workbook.AddSheet(table.Title, columns.Select(c => (ReportCatalog.Heading(c.Name), CellType(c.Type))).ToList());
            foreach (var row in store.ReadRows(table.Name))
                if (!sheet.WriteRow(columns.Select(c => Value(row, c)).ToList())) break;
        }
        return notes;
    }

    public static string StatusText(string status) => status == "CompletedWithIssues" ? "Completed with issues" : status;

    private static XlsxCell CellType(ColumnType type) => type switch
    {
        ColumnType.Number => XlsxCell.Number,
        ColumnType.Date => XlsxCell.Date,
        _ => XlsxCell.Text,
    };

    private static object? SiteCell(SiteRecord site, string column) => column switch
    {
        "site" => site.Site,
        "targetSite" => site.Target,
        "status" => SiteStatusText(site.Status),
        "rows" => site.Rows,
        "seconds" => site.Seconds,
        "finished" => site.FinishedUtc,
        "error" => site.Error,
        _ => null,
    };

    private static string SiteText(SiteRecord site, string column) => SiteCell(site, column) switch
    {
        null => "",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>A site's status in words.</summary>
    public static string SiteStatusText(string status) => status switch
    {
        "ok" => "Scanned",
        "denied" => "Access denied",
        "notFound" => "Not found",
        "signIn" => "Sign-in needed",
        "noConnection" => "No connection",
        "locked" => "Locked",
        "archived" => "Archived",
        "failed" => "Failed",
        _ => status,
    };

    /// <summary>A cell as a typed value for Excel.</summary>
    private static object? Value(Dictionary<string, JsonElement> row, ReportColumn column)
    {
        if (!row.TryGetValue(column.Name, out var value)) return null;
        switch (column.Type)
        {
            case ColumnType.Number when value.ValueKind == JsonValueKind.Number:
                return value.TryGetInt64(out var whole) ? whole : value.GetDouble();
            case ColumnType.Date when value.ValueKind == JsonValueKind.String
                                      && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date):
                return date;
            case ColumnType.Bool:
                return value.ValueKind == JsonValueKind.True;
            default:
                return ReportStore.CellText(value);
        }
    }

    /// <summary>A cell as text for CSV.</summary>
    private static string Text(Dictionary<string, JsonElement> row, ReportColumn column)
    {
        if (!row.TryGetValue(column.Name, out var value)) return "";
        return column.Type switch
        {
            ColumnType.Bool => value.ValueKind == JsonValueKind.True ? "Yes" : "",
            ColumnType.Date when value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => ReportStore.CellText(value),
        };
    }

    private static string CsvCell(string value, bool isText)
    {
        // A cell that starts like a formula would be run by a spreadsheet; a leading apostrophe makes it text.
        if (isText && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
