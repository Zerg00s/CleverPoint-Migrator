using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>
/// A migration job as a file: what the command line runs. One job copies one or more lists or
/// libraries from one site to another with the same settings.
///
/// The Description attributes are the documentation: the JSON Schema and the agent guide are
/// generated from them, so they stay in step with what the tool accepts.
/// </summary>
public sealed class JobSpec
{
    public const int CurrentSchemaVersion = 1;

    [Description("Version of this file format. Use 1.")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [Description("Name shown in run history. Optional.")]
    public string? Name { get; set; }

    [Description("Source site URL, for example https://contoso.sharepoint.com/sites/Finance. A saved connection must exist for its host.")]
    public string Source { get; set; } = "";

    [Description("Target site URL. Can be the same site, another site or another tenant.")]
    public string Target { get; set; } = "";

    [Description("full = create the list on the target, then copy content. content = copy content into a list that already exists. " +
                 "structure = create the list and its columns, copy no content. permissions = copy nothing, only apply the source's permissions to content already on the target.")]
    public string Mode { get; set; } = "full";

    [Description("classic = works for everything (default). migrationApi = Microsoft's Migration API, faster for very large libraries. It is limited: libraries only, " +
                 "latest version only, existing files are always imported again, no target folder, no permissions. A list or setting outside that is copied " +
                 "with the classic engine instead, and the run log says so; validate --online shows which engine each list will use.")]
    public string Engine { get; set; } = "classic";

    [Description("The lists and libraries to process, in order. Leave empty when allLists is true.")]
    public List<JobList> Lists { get; set; } = new();

    [Description("true = every visible list and library of the source site, each to a list of the same name. excludeLists removes some.")]
    public bool AllLists { get; set; }

    [Description("With allLists: titles or server-relative paths of lists to leave out.")]
    public List<string> ExcludeLists { get; set; } = new();

    [Description("Settings that apply to every list in the job.")]
    public JobOptions Options { get; set; } = new();

    [Description("Maps source users and groups to target users and groups. Optional; without it accounts are matched by sign-in name and email.")]
    public JobIdentityMapping? IdentityMapping { get; set; }
}

public sealed class JobList
{
    [Description("Title of the source list or library as the site shows it, for example Documents. Give this, sourcePath, or both.")]
    public string? Source { get; set; }

    [Description("Server-relative path of the source list's root folder, for example /sites/Finance/Shared Documents. Language-independent, so preferred when known.")]
    public string? SourcePath { get; set; }

    [Description("Title of the target list. Default: the source title. Required when source and target are the same site.")]
    public string? TargetTitle { get; set; }

    [Description("URL name for a target list that has to be created, for example Invoices or Lists/Invoices. Default: derived from the title.")]
    public string? TargetUrl { get; set; }

    [Description("Server-relative path of an existing target list's root folder. Preferred over targetTitle for a list that already exists.")]
    public string? TargetPath { get; set; }

    [Description("Server-relative path of a folder inside the source list. Only its contents are processed, and they land at the top of the target (or targetFolder) without the folders above it.")]
    public string? SourceFolder { get; set; }

    [Description("Folder inside the target list to copy into, relative to the list root, for example 2026/Imported. Classic engine only.")]
    public string? TargetFolder { get; set; }

    [Description("Server-relative paths of specific files and folders to process. A folder includes everything under it. Empty = everything.")]
    public List<string> Paths { get; set; } = new();

    [Description("IDs of specific list items to process (generic lists). Empty = everything.")]
    public List<int> ItemIds { get; set; } = new();

    [Description("Wildcards on the file or item name, for example *.pdf. An item is processed when it matches any pattern. Folders are always walked.")]
    public List<string> NamePatterns { get; set; } = new();
}

public sealed class JobOptions
{
    [Description("What to do when an item already exists on the target: overwrite, copyIfNewer or skip.")]
    public string Existing { get; set; } = "overwrite";

    [Description("File versions to copy. 1 = latest only. Up to 2000.")]
    public int Versions { get; set; } = 1;

    [Description("Keep Created, Modified, Created By and Modified By from the source.")]
    public bool PreserveAuthorsAndDates { get; set; } = true;

    [Description("Copy list item attachments.")]
    public bool CopyAttachments { get; set; } = true;

    [Description("Also copy permissions of lists, folders, files and items that have their own on the source. Slower: files are then copied one at a time.")]
    public bool CopyPermissions { get; set; }

    [Description("Permissions mode only: also restore inheritance on target items whose source item inherits. Removes permissions set by hand on the target.")]
    public bool RestoreInheritance { get; set; }

    [Description("Files copied at the same time within one library, 1 to 16.")]
    public int ParallelFileTransfers { get; set; } = 4;

    [Description("Which date modifiedSince and modifiedBefore compare against: modified or created.")]
    public string DateField { get; set; } = "modified";

    [Description("Only process items on or after this instant, ISO 8601, for example 2026-01-01T00:00:00Z.")]
    public DateTime? ModifiedSince { get; set; }

    [Description("Only process items before this instant, ISO 8601.")]
    public DateTime? ModifiedBefore { get; set; }

    [Description("Source column internal name to target column internal name, for columns that are named differently on the target.")]
    public Dictionary<string, string> FieldMap { get; set; } = new();
}

public sealed class JobIdentityMapping
{
    [Description("Path to a mapping CSV with the columns Type,Source,Target (Type is User or Group). Create one with: cpm mapping template.")]
    public string? Csv { get; set; }

    [Description("Source sign-in name or email to target sign-in name. Added on top of the CSV.")]
    public Dictionary<string, string> Users { get; set; } = new();

    [Description("Source SharePoint group name to target group name. Added on top of the CSV.")]
    public Dictionary<string, string> Groups { get; set; } = new();

    [Description("Target sign-in name that stands in for source accounts that cannot be matched, for example a service account.")]
    public string? Fallback { get; set; }

    [Description("Start from the mapping saved in the desktop app for this pair of tenants, if there is one. Default true.")]
    public bool UseSaved { get; set; } = true;
}

/// <summary>One problem found in a job file, phrased so that the fix is obvious.</summary>
public sealed record JobIssue(string Path, string Message);

/// <summary>Reading, checking and applying a <see cref="JobSpec"/>.</summary>
public static class JobSpecs
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A misspelled setting must be an error, not a setting that silently does nothing.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] Modes = { "full", "content", "structure", "permissions" };
    private static readonly string[] Engines = { "classic", "migrationApi" };
    private static readonly string[] ExistingModes = { "overwrite", "copyIfNewer", "skip" };
    private static readonly string[] DateFields = { "modified", "created" };

    /// <summary>Parses a job file. A file that is not valid JSON, or has an unknown setting, comes back as issues.</summary>
    public static JobSpec? Parse(string json, List<JobIssue> issues)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<JobSpec>(json, Json);
            if (spec is null)
            {
                issues.Add(new JobIssue("$", "The job file is empty."));
                return null;
            }
            // One spelling per site. History pairs source and target items by these addresses, so
            // "…/sites/A" in one job and "…/sites/A/" in the next must be the same site, or a
            // list copied twice would be duplicated instead of updated.
            spec.Source = NormalizeSite(spec.Source);
            spec.Target = NormalizeSite(spec.Target);
            return spec;
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path;
            var line = ex.LineNumber is { } n ? $" (line {n + 1})" : "";
            // The serializer's wording for a misspelled setting is about .NET members; say it plainly.
            var unknown = System.Text.RegularExpressions.Regex.Match(ex.Message, "JSON property '(?<name>[^']+)' could not be mapped");
            var message = unknown.Success
                ? $"Unknown setting '{unknown.Groups["name"].Value}'{line}. Check the spelling against: cpm schema job"
                : ex.Message.Split(" Path:")[0].Trim().TrimEnd('.') + line + ".";
            issues.Add(new JobIssue(where, message));
            return null;
        }
    }

    /// <summary>Everything that can be checked without contacting SharePoint.</summary>
    public static List<JobIssue> Validate(JobSpec spec)
    {
        var issues = new List<JobIssue>();
        void Bad(string path, string message) => issues.Add(new JobIssue(path, message));

        if (spec.SchemaVersion != JobSpec.CurrentSchemaVersion)
            Bad("schemaVersion", $"This version of the tool reads schemaVersion {JobSpec.CurrentSchemaVersion}; the file says {spec.SchemaVersion}.");
        var sourceOk = IsSiteUrl(spec.Source);
        var targetOk = IsSiteUrl(spec.Target);
        if (!sourceOk) Bad("source", "Give the source site as a full https URL, for example https://contoso.sharepoint.com/sites/Finance.");
        if (!targetOk) Bad("target", "Give the target site as a full https URL.");
        if (sourceOk && LooksLikePage(spec.Source)) Bad("source", "This is the address of a page, not of a site. Use the site, for example https://contoso.sharepoint.com/sites/Finance.");
        if (targetOk && LooksLikePage(spec.Target)) Bad("target", "This is the address of a page, not of a site. Use the site, for example https://contoso.sharepoint.com/sites/Finance.");
        if (!OneOf(spec.Mode, Modes)) Bad("mode", $"Use one of: {string.Join(", ", Modes)}.");
        if (!OneOf(spec.Engine, Engines)) Bad("engine", $"Use one of: {string.Join(", ", Engines)}.");
        if (!OneOf(spec.Options.Existing, ExistingModes)) Bad("options.existing", $"Use one of: {string.Join(", ", ExistingModes)}.");
        if (!OneOf(spec.Options.DateField, DateFields)) Bad("options.dateField", $"Use one of: {string.Join(", ", DateFields)}.");
        if (spec.Options.Versions is < 1 or > 2000) Bad("options.versions", "Use a number from 1 to 2000.");
        if (spec.Options.ParallelFileTransfers is < 1 or > 16) Bad("options.parallelFileTransfers", "Use a number from 1 to 16.");
        if (spec.Options is { ModifiedSince: { } since, ModifiedBefore: { } before } && since >= before)
            Bad("options.modifiedBefore", "modifiedBefore has to be later than modifiedSince.");
        if (spec.Options.RestoreInheritance && !Is(spec.Mode, "permissions"))
            Bad("options.restoreInheritance", "This only applies when mode is permissions.");


        if (spec.AllLists && spec.Lists.Count > 0) Bad("lists", "Use either allLists or lists, not both.");
        if (!spec.AllLists && spec.Lists.Count == 0) Bad("lists", "List at least one list or library, or set allLists to true.");
        if (!spec.AllLists && spec.ExcludeLists.Count > 0) Bad("excludeLists", "This only applies when allLists is true.");
        var sameSite = sourceOk && targetOk && SameSite(spec.Source, spec.Target);
        if (spec.AllLists && sameSite) Bad("allLists", "Source and target are the same site, so every list needs its own targetTitle. List them under lists instead.");

        for (var i = 0; i < spec.Lists.Count; i++)
        {
            var list = spec.Lists[i];
            var at = $"lists[{i}]";
            if (string.IsNullOrWhiteSpace(list.Source) && string.IsNullOrWhiteSpace(list.SourcePath))
                Bad(at, "Give source (the list title) or sourcePath (its server-relative path).");
            foreach (var (name, value) in new[] { ("sourcePath", list.SourcePath), ("targetPath", list.TargetPath), ("sourceFolder", list.SourceFolder) })
                if (!string.IsNullOrWhiteSpace(value) && !value!.StartsWith('/'))
                    Bad($"{at}.{name}", "Use a server-relative path that starts with /, for example /sites/Finance/Shared Documents.");
            if (list.Paths.Any(p => !p.StartsWith('/'))) Bad($"{at}.paths", "Each path has to be server-relative and start with /.");
            if (!string.IsNullOrWhiteSpace(list.TargetFolder) && list.TargetFolder!.StartsWith('/'))
                Bad($"{at}.targetFolder", "Give the folder relative to the list root, without a leading /, for example 2026/Imported.");
            if (sameSite && string.IsNullOrWhiteSpace(list.TargetTitle) && string.IsNullOrWhiteSpace(list.TargetPath))
                Bad($"{at}.targetTitle", "Source and target are the same site, so say which list to copy into (targetTitle or targetPath).");
        }

        if (spec.IdentityMapping?.Csv is { Length: > 0 } csv && !File.Exists(csv))
            Bad("identityMapping.csv", $"File not found: {csv}");
        return issues;
    }

    /// <summary>
    /// The engine options for one list of the job. <paramref name="sourceTitle"/> is the title the source
    /// site reports for the list, resolved by the caller.
    /// </summary>
    public static CopyOptions ToCopyOptions(JobSpec spec, JobList list, string sourceTitle)
    {
        var contentOnly = Is(spec.Mode, "content");
        var title = string.IsNullOrWhiteSpace(list.TargetTitle) ? sourceTitle : list.TargetTitle!.Trim();
        return new CopyOptions
        {
            TargetListTitle = title,
            TargetListUrl = string.IsNullOrWhiteSpace(list.TargetUrl) ? DefaultUrl(title) : list.TargetUrl!.Trim(),
            SourceListServerRelativeUrl = Blank(list.SourcePath),
            TargetListServerRelativeUrl = Blank(list.TargetPath),
            TargetSubfolderRelative = Blank(list.TargetFolder?.Trim('/')),
            SourceFolderServerRelativeUrl = Blank(list.SourceFolder),
            SelectedPaths = list.Paths.ToList(),
            ItemIds = list.ItemIds.ToList(),
            NamePatterns = list.NamePatterns.ToList(),
            PermissionsOnly = Is(spec.Mode, "permissions"),
            ResetTargetWhereSourceInherits = Is(spec.Mode, "permissions") && spec.Options.RestoreInheritance,
            CopyContent = !Is(spec.Mode, "structure"),
            MergeSchema = !contentOnly,
            CopyViews = !contentOnly,
            CopyListSettings = !contentOnly,
            PreserveAuthorsAndDates = spec.Options.PreserveAuthorsAndDates,
            CopyAttachments = spec.Options.CopyAttachments,
            CopyPermissions = spec.Options.CopyPermissions,
            ParallelFileTransfers = spec.Options.ParallelFileTransfers,
            MaxVersions = spec.Options.Versions,
            ExistingMode = spec.Options.Existing.ToLowerInvariant() switch
            {
                "copyifnewer" => ExistingItemMode.CopyIfNewer,
                "skip" => ExistingItemMode.Skip,
                _ => ExistingItemMode.Overwrite,
            },
            DateField = Is(spec.Options.DateField, "created") ? DateFilterField.Created : DateFilterField.Modified,
            ModifiedSinceUtc = spec.Options.ModifiedSince?.ToUniversalTime(),
            ModifiedBeforeUtc = spec.Options.ModifiedBefore?.ToUniversalTime(),
            FieldMap = new Dictionary<string, string>(spec.Options.FieldMap, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The identity mapping a job runs with: the saved one for the tenant pair, then the CSV, then the inline entries.</summary>
    public static IdentityMapping ResolveMapping(JobSpec spec)
    {
        var job = spec.IdentityMapping;
        var mapping = job is null || job.UseSaved ? new UxMappingStore().Load(spec.Source, spec.Target) : new IdentityMapping();
        if (job is null) return mapping;
        if (job.Csv is { Length: > 0 } csv) new UxMappingStore().ImportCsv(mapping, csv);
        foreach (var (source, target) in job.Users) mapping.Users[source] = target;
        foreach (var (source, target) in job.Groups) mapping.Groups[source] = target;
        if (!string.IsNullOrWhiteSpace(job.Fallback)) mapping.OrphanFallbackLogin = job.Fallback!.Trim();
        return mapping.Normalized();
    }

    /// <summary>The engine name the run service expects.</summary>
    public static string EngineName(JobSpec spec) => Is(spec.Engine, "migrationApi") ? "MigrationApi" : "Classic";

    /// <summary>An example job that passes validation, for an agent or a person to start from.</summary>
    public static JobSpec Example() => new()
    {
        Name = "Finance documents",
        Source = "https://contoso.sharepoint.com/sites/Finance",
        Target = "https://fabrikam.sharepoint.com/sites/Finance",
        Mode = "full",
        Lists =
        {
            new JobList { Source = "Documents" },
            new JobList { SourcePath = "/sites/Finance/Lists/Invoices", TargetTitle = "Invoices 2026" },
        },
        Options = new JobOptions { Existing = "copyIfNewer", Versions = 5, CopyPermissions = true },
        IdentityMapping = new JobIdentityMapping
        {
            Users = { ["anna@contoso.com"] = "anna@fabrikam.com" },
            Fallback = "migration@fabrikam.com",
        },
    };

    /// <summary>A site address without surrounding spaces, trailing slash, query or fragment.</summary>
    public static string NormalizeSite(string? url)
    {
        var text = (url ?? "").Trim();
        var cut = text.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) text = text[..cut];
        return text.TrimEnd('/');
    }

    public static bool SameSite(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>URL name for a new list: the letters and digits of its title, as the desktop app derives it.</summary>
    private static string DefaultUrl(string title) => string.Concat(title.Where(char.IsLetterOrDigit));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    private static bool OneOf(string? value, string[] allowed) => allowed.Any(a => Is(value, a));

    /// <summary>Copied from the browser's address bar while looking at a page or a library view.</summary>
    public static bool LooksLikePage(string url) =>
        url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) || url.Contains("/_layouts/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSiteUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host.Length > 0;
}
