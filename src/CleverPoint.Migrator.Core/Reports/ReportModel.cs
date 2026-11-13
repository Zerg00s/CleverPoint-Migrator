using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>What a report is run against.</summary>
public enum ReportScope
{
    /// <summary>The whole tenant, through its admin site. Takes a tenant, not a list of sites.</summary>
    Tenant,
    /// <summary>A list of sites, each scanned on its own.</summary>
    Sites,
    /// <summary>Pairs of source and target site.</summary>
    SitePairs,
}

public enum ColumnType { Text, Number, Date, Bool }

/// <summary>
/// One column of a report table. <see cref="Name"/> is the key in the row and the CSV header.
/// An <see cref="Optional"/> column is left out of exports and grids when no row of the run has a
/// value for it (a setting that was off, a feature the tenant does not have).
/// </summary>
public sealed record ReportColumn(string Name, ColumnType Type = ColumnType.Text, bool Optional = false);

/// <param name="SortBy">The column the app sorts the table by when it is first shown, largest first. In the files of a run the rows stay in the order they were found.</param>
public sealed record ReportTable(string Name, string Title, IReadOnlyList<ReportColumn> Columns, string? SortBy = null);

/// <summary>
/// A setting of a report. <see cref="Type"/> is bool, int, text or list (of text). A text setting with
/// <see cref="Choices"/> takes one of those values and nothing else.
/// </summary>
/// <param name="Accounts">A list setting that holds people and groups; the app offers a directory search for it.</param>
/// <param name="When">"setting=value": the setting only matters when that other setting has that value, and the app shows it only then.</param>
public sealed record ReportOption(string Name, string Type, object? Default, string Help, IReadOnlyList<string>? Choices = null, bool Accounts = false, string? When = null);

/// <summary>One unit of work: a site, or a source and target pair.</summary>
public sealed record SiteTarget(string Url, string? TargetUrl = null)
{
    /// <summary>What identifies this unit in the run's records.</summary>
    public string Key => TargetUrl is null ? Url : Url + " -> " + TargetUrl;
}

/// <summary>
/// One report: what it is called, what it needs, what its rows look like, and the code that scans one
/// site. The catalog, the command-line help, the JSON schema and the desktop page are all generated
/// from these definitions, so adding a report is adding one class.
/// </summary>
public abstract class ReportDefinition
{
    /// <summary>The name used on the command line and in report files, for example "lists".</summary>
    public abstract string Id { get; }
    public abstract string Title { get; }
    /// <summary>Inventory, Permissions, Compare, Discovery or Actions.</summary>
    public abstract string Group { get; }
    public abstract string Summary { get; }
    public virtual ReportScope Scope => ReportScope.Sites;
    /// <summary>Needs a connection that can reach the tenant's admin site.</summary>
    public virtual bool NeedsTenantAdmin => false;
    /// <summary>An action: it changes sites. Without "apply" it only records what it would change.</summary>
    public virtual bool ChangesData => false;
    /// <summary>For an action that can do something that cannot be undone: what to say before it is applied.</summary>
    public virtual string? ApplyWarning => null;
    /// <summary>
    /// For a report that is meant for one kind of site: "onedrive" makes "every site of a tenant" start
    /// on OneDrives where the report is set up. It still runs on any site it is given.
    /// </summary>
    public virtual string? DefaultSiteType => null;
    public abstract IReadOnlyList<ReportTable> Tables { get; }
    public virtual IReadOnlyList<ReportOption> Options => Array.Empty<ReportOption>();

    /// <summary>Scans one site (or the tenant, or one pair) and writes its rows through the context.</summary>
    public abstract Task RunAsync(ReportContext context);

    /// <summary>
    /// What is wrong with the settings as a whole (a required one missing, two that contradict). Checked
    /// before a run is started, so the mistake is reported at once instead of once per site.
    /// </summary>
    public virtual IEnumerable<string> CheckOptions(ReportOptions options) => Array.Empty<string>();

    // ---- helpers for declaring tables ----------------------------------------------------------

    protected static ReportTable Table(string name, string title, params ReportColumn[] columns) => new(name, title, columns);
    protected static ReportColumn Text(string name) => new(name);
    protected static ReportColumn Number(string name) => new(name, ColumnType.Number);
    protected static ReportColumn Date(string name) => new(name, ColumnType.Date);
    protected static ReportColumn Flag(string name) => new(name, ColumnType.Bool);
    /// <summary>Marks a column as one to leave out when the whole run has nothing in it.</summary>
    protected static ReportColumn IfAny(ReportColumn column) => column with { Optional = true };
}

/// <summary>The settings of one run: what the report file gave, over the report's defaults.</summary>
public sealed class ReportOptions
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ReportOptions(ReportDefinition report, IReadOnlyDictionary<string, JsonElement>? given)
    {
        foreach (var option in report.Options) _values[option.Name] = option.Default;
        if (given is null) return;
        foreach (var (name, value) in given)
        {
            var option = report.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (option is null) continue;       // unknown names are reported by validation, not here
            _values[option.Name] = Convert(option, value);
        }
    }

    /// <summary>Null when the value fits the option's type, else what is wrong with it.</summary>
    public static string? Check(ReportOption option, JsonElement value) => option.Type switch
    {
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "has to be true or false",
        "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _) ? null : "has to be a whole number",
        "list" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
            ? null : "has to be a list of texts",
        _ => value.ValueKind != JsonValueKind.String ? "has to be a text"
            : option.Choices is { } choices && !choices.Contains(value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
                ? "has to be one of: " + string.Join(", ", choices)
                : null,
    };

    private static object? Convert(ReportOption option, JsonElement value) => Check(option, value) is not null
        ? option.Default
        : option.Type switch
        {
            "bool" => value.GetBoolean(),
            "int" => value.GetInt32(),
            "list" => value.EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
            _ => value.GetString(),
        };

    public bool Bool(string name) => _values.GetValueOrDefault(name) is true;
    public int Int(string name) => _values.GetValueOrDefault(name) is int number ? number : 0;
    public string Text(string name) => _values.GetValueOrDefault(name) as string ?? "";
    public IReadOnlyList<string> List(string name) => _values.GetValueOrDefault(name) as List<string> ?? new List<string>();
    public IReadOnlyDictionary<string, object?> All => _values;
}

/// <summary>
/// What a report's code gets for one site: the connection, the settings, and where to put rows.
/// Rows written here reach the report's tables only when the site finishes without an error, so a
/// site that fails halfway leaves nothing behind and can simply be run again.
/// </summary>
public sealed class ReportContext
{
    /// <summary>The site being scanned. For a tenant report: the admin site.</summary>
    public required SiteTarget Site { get; init; }
    /// <summary>A connection to <see cref="Site"/>.</summary>
    public required SpConnection Connection { get; init; }
    /// <summary>For pairs: a connection to the target site.</summary>
    public SpConnection? Target { get; init; }
    public required ReportOptions Options { get; init; }
    /// <summary>Actions only: true makes the change, false records what would change.</summary>
    public bool Apply { get; init; }
    public CancellationToken Cancel { get; init; }
    /// <summary>A connection to another site, for a report that has to look beyond its own.</summary>
    public required Func<string, Task<SpConnection>> Connect { get; init; }

    internal Action<string, Dictionary<string, object?>> RowSink { get; init; } = (_, _) => { };
    internal Action<string> PhaseSink { get; init; } = _ => { };
    internal Dictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds a row to one of the report's tables.</summary>
    public void Write(string table, Dictionary<string, object?> row) => RowSink(table, row);

    /// <summary>Adds to one of the run's summary numbers, for example "lists" or "guests".</summary>
    public void Count(string name, long by = 1)
    {
        lock (Counters) Counters[name] = Counters.GetValueOrDefault(name) + by;
    }

    /// <summary>What this site is doing right now, for anyone watching the run.</summary>
    public void Phase(string text) => PhaseSink(text);
}

/// <summary>Small readers for the JSON that SharePoint REST returns.</summary>
public static class JsonRead
{
    public static string Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            }
            : "";

    public static long Num(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out var whole) ? whole : (long)value.GetDouble();
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? (long)parsed : 0;
    }

    public static int? NumOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble() : null;

    public static bool Bool(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A date as UTC, or null when the field is missing, empty or the "no date" placeholder.</summary>
    public static DateTime? When(this JsonElement element, string name)
    {
        var text = element.Str(name);
        if (text.Length == 0) return null;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) return null;
        return date.Year < 1900 ? null : date;
    }

    public static Guid Id(this JsonElement element, string name) => Guid.TryParse(element.Str(name), out var id) ? id : Guid.Empty;

    /// <summary>A nested object, or an empty element when it is missing.</summary>
    public static JsonElement Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
