using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// A report run as a file: which report, against which sites, with which settings. The command line
/// runs these, and the desktop app builds one from its Reports page.
///
/// The Description attributes are the documentation: the JSON Schema is generated from them.
/// </summary>
public sealed class ReportSpec
{
    public const int CurrentSchemaVersion = 1;

    [Description("Version of this file format. Use 1.")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [Description("Which report to run. See the IDs with: cpm report list")]
    public string Report { get; set; } = "";

    [Description("Name shown in the list of report runs. Optional.")]
    public string? Name { get; set; }

    [Description("Site URLs to scan, for example https://contoso.sharepoint.com/sites/Finance. For reports with scope sites.")]
    public List<string> Sites { get; set; } = new();

    [Description("A text or CSV file with one site URL per line (first column of a CSV; a header line is skipped). Relative to this file.")]
    public string? SitesFile { get; set; }

    [Description("Scan every site of a tenant instead of listing them. Needs a connection that can reach the tenant's admin site.")]
    public ReportAllSites? AllSites { get; set; }

    [Description("Take the sites from an earlier run of the sites or onedrives report: its run ID.")]
    public string? FromReport { get; set; }

    [Description("Source and target site pairs. For reports with scope sitePairs.")]
    public List<ReportPair> Pairs { get; set; } = new();

    [Description("A CSV file with two columns, source URL and target URL (a header line is skipped). Relative to this file.")]
    public string? PairsFile { get; set; }

    [Description("Any site of the tenant, for example https://contoso.sharepoint.com. For reports with scope tenant.")]
    public string? Tenant { get; set; }

    [Description("The report's own settings. See them with: cpm report list --report <id>")]
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [Description("How many sites are scanned at the same time, 1 to 8. Default 4.")]
    public int Parallel { get; set; } = 4;

    [Description("Actions only. false (default) records what would change and changes nothing. true makes the changes.")]
    public bool Apply { get; set; }
}

public sealed class ReportAllSites
{
    [Description("Any site of the tenant, for example https://contoso.sharepoint.com.")]
    public string Tenant { get; set; } = "";

    [Description("sharepoint (default) = site collections, onedrive = OneDrives, all = both.")]
    public string Type { get; set; } = "sharepoint";

    [Description("Only sites whose URL contains this text. Optional.")]
    public string? UrlContains { get; set; }

    [Description("Only sites with this template, for example GROUP#0 or SITEPAGEPUBLISHING#0. Optional.")]
    public string? Template { get; set; }
}

public sealed class ReportPair
{
    [Description("Source site URL.")]
    public string Source { get; set; } = "";

    [Description("Target site URL.")]
    public string Target { get; set; } = "";
}

/// <summary>One thing wrong with a report file, with the place it is in.</summary>
public sealed record ReportIssue(string Path, string Message);

public static class ReportSpecs
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const int MaxParallel = 8;

    /// <summary>Reads a report file. Null with the reason in <paramref name="issues"/> when it is not valid JSON for this format.</summary>
    public static ReportSpec? Parse(string json, List<ReportIssue> issues)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<ReportSpec>(json, Json);
            if (spec is null) issues.Add(new ReportIssue("", "The file is empty."));
            return spec;
        }
        catch (JsonException ex)
        {
            var where = ex.Path is { Length: > 0 } path ? path.TrimStart('$', '.') : "";
            issues.Add(new ReportIssue(where, ex.Message.Split(" Path:")[0].Split(" LineNumber:")[0].Trim()));
            return null;
        }
    }

    /// <summary>Everything wrong with a report file that can be told without going online.</summary>
    public static List<ReportIssue> Validate(ReportSpec spec, string? baseFolder = null)
    {
        var issues = new List<ReportIssue>();
        if (spec.SchemaVersion != ReportSpec.CurrentSchemaVersion)
            issues.Add(new ReportIssue("schemaVersion", $"Version {spec.SchemaVersion} is not known. Use {ReportSpec.CurrentSchemaVersion}."));

        var report = ReportCatalog.Find(spec.Report);
        if (report is null)
        {
            issues.Add(new ReportIssue("report", string.IsNullOrWhiteSpace(spec.Report)
                ? "Name the report to run. See the IDs with: cpm report list"
                : $"There is no report '{spec.Report}'. See the IDs with: cpm report list"));
            return issues;
        }

        if (spec.Parallel is < 1 or > MaxParallel)
            issues.Add(new ReportIssue("parallel", $"Has to be between 1 and {MaxParallel}."));
        if (spec.Apply && !report.ChangesData)
            issues.Add(new ReportIssue("apply", $"'{report.Id}' only reads; apply is for actions."));

        foreach (var (name, value) in spec.Options)
        {
            var option = report.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (option is null)
                issues.Add(new ReportIssue($"options.{name}", report.Options.Count == 0
                    ? $"'{report.Id}' has no settings."
                    : $"'{report.Id}' has no setting '{name}'. It has: {string.Join(", ", report.Options.Select(o => o.Name))}."));
            else if (ReportOptions.Check(option, value) is { } problem)
                issues.Add(new ReportIssue($"options.{name}", $"'{name}' {problem}."));
        }

        if (issues.Count == 0)
            foreach (var problem in report.CheckOptions(new ReportOptions(report, spec.Options)))
                issues.Add(new ReportIssue("options", problem));

        var siteSources = new[]
        {
            spec.Sites.Count > 0 ? "sites" : null,
            spec.SitesFile is not null ? "sitesFile" : null,
            spec.AllSites is not null ? "allSites" : null,
            spec.FromReport is not null ? "fromReport" : null,
        }.Where(s => s is not null).ToList();
        var pairSources = new[] { spec.Pairs.Count > 0 ? "pairs" : null, spec.PairsFile is not null ? "pairsFile" : null }.Where(s => s is not null).ToList();

        switch (report.Scope)
        {
            case ReportScope.Tenant:
                if (string.IsNullOrWhiteSpace(spec.Tenant))
                    issues.Add(new ReportIssue("tenant", $"'{report.Id}' covers a whole tenant. Give any site of it in tenant."));
                else CheckSite(spec.Tenant, "tenant", issues);
                foreach (var extra in siteSources.Concat(pairSources))
                    issues.Add(new ReportIssue(extra!, $"'{report.Id}' covers a whole tenant and takes no list of sites. Remove {extra}."));
                break;

            case ReportScope.Sites:
                if (siteSources.Count == 0)
                    issues.Add(new ReportIssue("sites", "Say which sites to scan: sites, sitesFile, allSites or fromReport."));
                if (!string.IsNullOrWhiteSpace(spec.Tenant))
                    issues.Add(new ReportIssue("tenant", $"'{report.Id}' runs per site and takes no tenant. To scan a whole tenant use allSites."));
                if (siteSources.Count > 1)
                    issues.Add(new ReportIssue(siteSources[1]!, $"Use one of {string.Join(" and ", siteSources)}, not both."));
                foreach (var extra in pairSources)
                    issues.Add(new ReportIssue(extra!, $"'{report.Id}' scans single sites, not pairs. Remove {extra}."));
                for (var i = 0; i < spec.Sites.Count; i++) CheckSite(spec.Sites[i], $"sites[{i}]", issues);
                if (spec.SitesFile is not null) CheckFile(spec.SitesFile, "sitesFile", baseFolder, issues);
                if (spec.AllSites is { } all)
                {
                    if (string.IsNullOrWhiteSpace(all.Tenant)) issues.Add(new ReportIssue("allSites.tenant", "Give any site of the tenant."));
                    else CheckSite(all.Tenant, "allSites.tenant", issues);
                    if (all.Type.ToLowerInvariant() is not ("sharepoint" or "onedrive" or "all"))
                        issues.Add(new ReportIssue("allSites.type", "Has to be sharepoint, onedrive or all."));
                }
                break;

            case ReportScope.SitePairs:
                if (pairSources.Count == 0)
                    issues.Add(new ReportIssue("pairs", "Say which sites to compare: pairs or pairsFile."));
                if (!string.IsNullOrWhiteSpace(spec.Tenant))
                    issues.Add(new ReportIssue("tenant", $"'{report.Id}' works on pairs of sites and takes no tenant."));
                if (pairSources.Count > 1)
                    issues.Add(new ReportIssue("pairsFile", "Use pairs or pairsFile, not both."));
                foreach (var extra in siteSources)
                    issues.Add(new ReportIssue(extra!, $"'{report.Id}' works on source and target pairs. Use pairs or pairsFile instead of {extra}."));
                for (var i = 0; i < spec.Pairs.Count; i++)
                {
                    CheckSite(spec.Pairs[i].Source, $"pairs[{i}].source", issues);
                    CheckSite(spec.Pairs[i].Target, $"pairs[{i}].target", issues);
                }
                if (spec.PairsFile is not null) CheckFile(spec.PairsFile, "pairsFile", baseFolder, issues);
                break;
        }
        return issues;
    }

    private static void CheckSite(string value, string path, List<ReportIssue> issues)
    {
        if (SiteProblem(value) is { } problem) issues.Add(new ReportIssue(path, problem));
    }

    /// <summary>Null when the text is a site URL, else what is wrong with it.</summary>
    public static string? SiteProblem(string value)
    {
        var url = JobSpecs.NormalizeSite(value ?? "");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.Host.Length == 0)
            return $"'{value}' is not a site URL. Use a full https URL.";
        return JobSpecs.LooksLikePage(url) ? $"'{value}' is the address of a page, not of a site." : null;
    }

    private static void CheckFile(string file, string path, string? baseFolder, List<ReportIssue> issues)
    {
        var full = ResolveFile(file, baseFolder);
        if (!File.Exists(full)) issues.Add(new ReportIssue(path, $"The file does not exist: {full}"));
    }

    public static string ResolveFile(string file, string? baseFolder) =>
        Path.IsPathRooted(file) || baseFolder is null ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(baseFolder, file));

    /// <summary>
    /// Reads site URLs from a text or CSV file: one per line, the first column when there are several.
    /// Lines that are not URLs (a header, blanks, comments) are skipped and counted.
    /// </summary>
    public static (List<string> Sites, int Skipped) ReadSitesFile(string path)
    {
        var sites = new List<string>();
        var skipped = 0;
        foreach (var line in File.ReadLines(path))
        {
            var first = SplitCsv(line).FirstOrDefault()?.Trim() ?? "";
            if (first.Length == 0 || first.StartsWith('#')) continue;
            if (SiteProblem(first) is null) sites.Add(JobSpecs.NormalizeSite(first));
            else skipped++;
        }
        return (Distinct(sites), skipped);
    }

    /// <summary>Reads source,target pairs from a CSV file. Lines without two URLs are skipped and counted.</summary>
    public static (List<ReportPair> Pairs, int Skipped) ReadPairsFile(string path)
    {
        var pairs = new List<ReportPair>();
        var skipped = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var cells = SplitCsv(line);
            if (cells.Count >= 2 && SiteProblem(cells[0].Trim()) is null && SiteProblem(cells[1].Trim()) is null)
                pairs.Add(new ReportPair { Source = JobSpecs.NormalizeSite(cells[0].Trim()), Target = JobSpecs.NormalizeSite(cells[1].Trim()) });
            else skipped++;
        }
        return (pairs, skipped);
    }

    public static List<string> Distinct(IEnumerable<string> sites)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sites.Select(JobSpecs.NormalizeSite).Where(seen.Add).ToList();
    }

    /// <summary>A CSV line split on commas (or semicolons or tabs), honoring double quotes.</summary>
    public static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c is ',' or ';' or '\t') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>The admin site of the tenant a site belongs to: contoso, contoso-my and contoso-admin all give contoso-admin.</summary>
    public static string AdminUrl(string anySiteOfTheTenant)
    {
        var host = new Uri(JobSpecs.NormalizeSite(anySiteOfTheTenant)).Host;
        var dot = host.IndexOf('.');
        var (name, rest) = dot < 0 ? (host, "") : (host[..dot], host[dot..]);
        if (name.EndsWith("-my", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        else if (name.EndsWith("-admin", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return $"https://{name}-admin{rest}".ToLowerInvariant();
    }

    /// <summary>A report file to start from: the report's settings at their defaults and a placeholder for the sites.</summary>
    public static string Template(ReportDefinition report)
    {
        var spec = new Dictionary<string, object?>
        {
            ["schemaVersion"] = ReportSpec.CurrentSchemaVersion,
            ["report"] = report.Id,
            ["name"] = report.Title,
        };
        switch (report.Scope)
        {
            case ReportScope.Tenant:
                spec["tenant"] = "https://contoso.sharepoint.com";
                break;
            case ReportScope.Sites:
                spec["sites"] = new[] { "https://contoso.sharepoint.com/sites/Finance", "https://contoso.sharepoint.com/sites/HR" };
                break;
            case ReportScope.SitePairs:
                spec["pairs"] = new[] { new { source = "https://contoso.sharepoint.com/sites/Finance", target = "https://fabrikam.sharepoint.com/sites/Finance" } };
                break;
        }
        if (report.Options.Count > 0) spec["options"] = report.Options.ToDictionary(o => o.Name, o => o.Default);
        if (report.Scope != ReportScope.Tenant) spec["parallel"] = 4;
        if (report.ChangesData) spec["apply"] = false;
        return JsonSerializer.Serialize(spec, Json);
    }
}
