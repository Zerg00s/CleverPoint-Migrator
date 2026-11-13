using CleverPoint.Migrator.Core.Reports.Definitions;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>Every report the tool can run. Adding a report is adding its class here.</summary>
public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDefinition> All = new ReportDefinition[]
    {
        new SitesReport(),
        new OneDrivesReport(),
        new ListsReport(),
        new StorageReport(),
        new VersionLimitsReport(),
        new DocumentsReport(),
        new FileVersionsReport(),
        new PermissionsReport(),
        new GuestsReport(),
        new SiteAccessReport(),
        new SubsitesReport(),
        new PublishingReport(),
        new CustomizedListsReport(),
        new CustomColumnsReport(),
        new BrokenNotebooksReport(),
        new OneDriveListsReport(),
        new OneDrivePagesReport(),
        new SiteAppCatalogsReport(),
        new ListCompareReport(),
        new DocumentCompareReport(),
        new NavigationCompareReport(),
        new PermissionsCompareReport(),
        new SiteLockAction(),
        new SiteAdminsAction(),
        new NavSyncAction(),
        new FixNotebooksAction(),
        new SetVersionLimitsAction(),
    };

    public static ReportDefinition? Find(string? id) =>
        All.FirstOrDefault(r => r.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The columns of a table as they are stored and exported: the site the row came from first (the
    /// runner adds it to every row), then the report's own columns.
    /// </summary>
    /// <param name="used">
    /// The columns that have a value somewhere in a run (<see cref="ReportStore.UsedColumns"/>). When
    /// given, optional columns that are not among them are left out.
    /// </param>
    public static IReadOnlyList<ReportColumn> Columns(ReportDefinition report, ReportTable table, IReadOnlySet<string>? used = null)
    {
        var columns = new List<ReportColumn>();
        if (report.Scope != ReportScope.Tenant) columns.Add(new ReportColumn("site"));
        if (report.Scope == ReportScope.SitePairs) columns.Add(new ReportColumn("targetSite"));
        columns.AddRange(table.Columns.Where(c => !c.Optional || used is null || used.Contains(c.Name)));
        return columns;
    }

    /// <summary>
    /// A setting's name as a short label for a form. The full explanation (its Help) is for the command
    /// line and for a tooltip; a screen full of sentences is harder to use than a few words per box.
    /// </summary>
    /// <summary>For the few settings a label cannot explain: a line shown under the field where the report is set up.</summary>
    public static string? OptionNote(ReportOption option) => option.Name switch
    {
        "largestFiles" => "Smaller files are still counted in every total. They are just not listed one by one.",
        _ => null,
    };

    public static string OptionLabel(ReportOption option) => option.Name switch
    {
        "extensions" => "File extensions",
        "limits" => "Version limits",
        "majorVersions" => "Versions to keep",
        "expireAfterDays" => "Delete versions older than (days, 0 = never)",
        "existingLibraries" => "Also change existing libraries",
        "trim" => "Delete existing versions",
        "trimDays" => "Older than (days)",
        "trimKeep" => "Versions to keep of each file",
        "folderLevels" => "Folder level columns",
        "includeHidden" => "Include hidden lists and libraries",
        "includeSubsites" => "Include subsites",
        "includeDeleted" => "Deleted sites",
        "includeSystem" => "System sites",
        "customizedOnly" => "Customized only",
        "brokenOnly" => "Broken only",
        "versionRows" => "Row per version",
        "onlyWithHistory" => "Only files with history",
        "includeItems" => "Folders, files and items",
        "sharingLinks" => "Sharing links",
        "linkUrls" => "Link addresses",
        "expandGroups" => "Group members",
        "limitedAccess" => "Limited Access",
        "tolerancePercent" => "Tolerance, %",
        "toleranceItems" => "Tolerance, items",
        "includeSiteAssets" => "Site Assets",
        "exclude" => "Exclude (titles)",
        "includeMatches" => "Matches too",
        "includeLists" => "Lists and libraries",
        "includeTemplates" => "Templates and system pages",
        "includeSitesWithout" => "Sites without a catalog",
        "largestFiles" => "Number of biggest files to list, per site",
        "recycleBin" => "Measure the recycle bin",
        "includeFolders" => "Folders",
        "includePages" => "Pages (.aspx)",
        "looseNames" => "Ignore replaced characters",
        "state" => "Lock state",
        "add" => "Add as administrator",
        "remove" => "Remove as administrator",
        "kind" => "Which guests",
        _ => Heading(option.Name),
    };

    /// <summary>The first few words of a report's summary: what it lists, without the detail.</summary>
    public static string ShortSummary(ReportDefinition report)
    {
        var text = report.Summary;
        var cut = new[] { text.IndexOf(':'), text.IndexOf('.'), text.IndexOf(','), text.IndexOf(" (", StringComparison.Ordinal) }.Where(i => i > 0).DefaultIfEmpty(-1).Min();
        return cut > 0 ? text[..cut] : text;
    }

    public static string ScopeName(ReportScope scope) => scope switch
    {
        ReportScope.Tenant => "tenant",
        ReportScope.SitePairs => "sitePairs",
        _ => "sites",
    };

    /// <summary>A column name as a heading for people: "storageUsedGB" becomes "Storage used GB".</summary>
    public static string Heading(string column)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        for (var i = 0; i < column.Length; i++)
        {
            var c = column[i];
            // A new word starts at a capital that follows a lower-case letter, or that begins a lower-case run ("usedGB" + "Total").
            var boundary = i > 0 && char.IsUpper(c)
                           && (char.IsLower(column[i - 1]) || char.IsDigit(column[i - 1]) || (i + 1 < column.Length && char.IsLower(column[i + 1])));
            if (boundary) { words.Add(word.ToString()); word.Clear(); }
            word.Append(c);
        }
        words.Add(word.ToString());
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var acronym = w.Length > 1 && w.All(char.IsUpper);
            words[i] = w.ToLowerInvariant() switch
            {
                "url" => "URL",
                "id" => "ID",
                "spfx" => "SPFx",
                _ => acronym ? w : i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant(),
            };
        }
        return string.Join(' ', words);
    }
}
