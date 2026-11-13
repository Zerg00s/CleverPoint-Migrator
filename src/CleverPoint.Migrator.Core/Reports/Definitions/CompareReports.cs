using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>What the two compare reports share: pairing the lists of a source site with those of its target.</summary>
internal static class ListPairing
{
    /// <summary>Lists SharePoint makes for itself, which say nothing about whether content arrived.</summary>
    public static readonly string[] SystemLists =
    {
        "MicroFeed", "Style Library", "appdata", "TaxonomyHiddenList", "Composed Looks", "Master Page Gallery", "Solution Gallery",
        "Theme Gallery", "Web Part Gallery", "Workflow Tasks", "User Information List", "Converted Forms", "Customized Reports",
        "Form Templates", "Content type publishing error log", "Team Message History", "Channel Settings", "Site Collection Documents",
        "Site Collection Images", "Preservation Hold Library",
    };

    public sealed record ListInfo(string Id, string Title, string RootFolder, string RelativePath, long Items, long Template, bool IsLibrary);

    public static async Task<List<ListInfo>> ListsAsync(SpConnection connection, bool includeHidden, CancellationToken ct)
    {
        // The site's address is URL-encoded; the paths SharePoint returns for lists and files are not.
        var webPath = Uri.UnescapeDataString(new Uri(connection.SiteUrl).AbsolutePath).TrimEnd('/');
        var lists = await connection.Rest.GetAllAsync(
            $"{connection.SiteUrl}/_api/web/lists?$select=Id,Title,Hidden,ItemCount,BaseTemplate,BaseType,RootFolder/ServerRelativeUrl&$expand=RootFolder&$top=500", ct);
        return lists
            .Where(l => includeHidden || !l.Bool("Hidden"))
            .Select(l =>
            {
                var root = l.Child("RootFolder").Str("ServerRelativeUrl");
                var relative = root.StartsWith(webPath + "/", StringComparison.OrdinalIgnoreCase) ? root[(webPath.Length + 1)..] : root.TrimStart('/');
                return new ListInfo(l.Str("Id"), l.Str("Title"), root, relative, l.Num("ItemCount"), l.Num("BaseTemplate"), l.Num("BaseType") == 1);
            })
            .ToList();
    }

    /// <summary>
    /// Pairs source lists with target lists: by title, and failing that by address within the site,
    /// which still matches when the two sites are in different languages ("Documents" and "Dokumente"
    /// are both at Shared Documents).
    /// </summary>
    public static List<(ListInfo? Source, ListInfo? Target)> Pair(List<ListInfo> source, List<ListInfo> target)
    {
        var pairs = new List<(ListInfo?, ListInfo?)>();
        var left = new List<ListInfo>(target);
        foreach (var list in source)
        {
            var match = left.FirstOrDefault(t => t.Title.Equals(list.Title, StringComparison.OrdinalIgnoreCase))
                        ?? left.FirstOrDefault(t => t.RelativePath.Equals(list.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (match is not null) left.Remove(match);
            pairs.Add((list, match));
        }
        pairs.AddRange(left.Select(t => ((ListInfo?)null, (ListInfo?)t)));
        return pairs;
    }

    public static HashSet<string> Excluded(ReportContext context, bool siteAssets)
    {
        var excluded = new HashSet<string>(SystemLists, StringComparer.OrdinalIgnoreCase);
        if (!siteAssets) excluded.Add("Site Assets");
        foreach (var title in context.Options.List("exclude")) excluded.Add(title.Trim());
        return excluded;
    }
}

/// <summary>Source site against target site, list by list, by item count.</summary>
public sealed class ListCompareReport : ReportDefinition
{
    public override string Id => "list-compare";
    public override string Title => "List compare";
    public override string Group => "Compare";
    public override string Summary => "Source site against target site, list by list: item counts on both sides, and which lists exist on one side only. A quick check that a migration is complete.";
    public override ReportScope Scope => ReportScope.SitePairs;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("tolerancePercent", "int", 0, "Counts that differ by no more than this percentage still count as a match. 0 means the counts have to be equal."),
        new ReportOption("toleranceItems", "int", 0, "Counts that differ by no more than this many items still count as a match."),
        new ReportOption("includeSiteAssets", "bool", false, "Also compare the Site Assets library."),
        new ReportOption("includeHidden", "bool", false, "Also compare hidden lists."),
        new ReportOption("exclude", "list", new List<string>(), "Titles of lists to leave out."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("lists", "Lists",
            Text("listTitle"), Text("listType"), Text("status"), Number("sourceCount"), Number("targetCount"), Number("difference"),
            Number("percentDifference"), Text("sourceUrl"), Text("targetUrl")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var target = context.Target ?? throw new InvalidOperationException("This report needs a target site.");
        var excluded = ListPairing.Excluded(context, context.Options.Bool("includeSiteAssets"));
        var hidden = context.Options.Bool("includeHidden");
        var source = (await ListPairing.ListsAsync(context.Connection, hidden, context.Cancel)).Where(l => !excluded.Contains(l.Title)).ToList();
        var targetLists = (await ListPairing.ListsAsync(target, hidden, context.Cancel)).Where(l => !excluded.Contains(l.Title)).ToList();
        var sourceAuthority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        var targetAuthority = new Uri(target.SiteUrl).GetLeftPart(UriPartial.Authority);
        var percent = context.Options.Int("tolerancePercent");
        var items = context.Options.Int("toleranceItems");

        foreach (var (left, right) in ListPairing.Pair(source, targetLists))
        {
            var difference = (right?.Items ?? 0) - (left?.Items ?? 0);
            string status;
            if (left is null) status = "Target only";
            else if (right is null) status = "Source only";
            else
            {
                var within = difference == 0 || Math.Abs(difference) <= items
                             || (left.Items > 0 && Math.Abs(difference) * 100.0 / left.Items <= percent);
                status = within ? "Match" : "Mismatch";
            }
            context.Write("lists", new Dictionary<string, object?>
            {
                ["listTitle"] = (left ?? right)!.Title,
                ["listType"] = ListTypes.Name((left ?? right)!.Template),
                ["status"] = status,
                ["sourceCount"] = left?.Items,
                ["targetCount"] = right?.Items,
                ["difference"] = left is not null && right is not null ? difference : null,
                ["percentDifference"] = left is { Items: > 0 } && right is not null ? Math.Round(difference * 100.0 / left.Items, 1) : null,
                ["sourceUrl"] = left is null ? null : sourceAuthority + left.RootFolder,
                ["targetUrl"] = right is null ? null : targetAuthority + right.RootFolder,
            });
            context.Count("lists");
            context.Count(status switch { "Match" => "match", "Mismatch" => "mismatch", "Source only" => "sourceOnly", _ => "targetOnly" });
        }
    }
}

/// <summary>Source site against target site, file by file.</summary>
public sealed class DocumentCompareReport : ReportDefinition
{
    /// <summary>SharePoint's limit for the full address of a file.</summary>
    private const int MaxPath = 400;

    public override string Id => "document-compare";
    public override string Title => "Document compare";
    public override string Group => "Compare";
    public override string Summary => "Source site against target site, file by file: missing on the target, extra on the target, empty or much smaller than the source, newer at the source, path too long. Lists the problems, not the files that are fine.";
    public override ReportScope Scope => ReportScope.SitePairs;

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeMatches", "bool", false, "Also write a row for every file that is fine. Makes the report as large as the libraries."),
        new ReportOption("includeFolders", "bool", true, "Compare folders as well as files, which shows empty folders that did not arrive."),
        new ReportOption("includePages", "bool", false, "Also compare .aspx pages."),
        new ReportOption("looseNames", "bool", false, "Treat two names as the same when they differ only in characters that tools replace with an underscore during a migration (# % & ~ { } and the like)."),
        new ReportOption("includeSiteAssets", "bool", false, "Also compare the Site Assets library."),
        new ReportOption("includeHidden", "bool", false, "Also compare hidden libraries."),
        new ReportOption("exclude", "list", new List<string>(), "Titles of libraries to leave out."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("differences", "Differences",
            Text("library"), Text("itemType"), Text("path"), Text("status"), IfAny(Text("note")), Number("sourceSizeBytes"), Number("targetSizeBytes"),
            Date("sourceModified"), Date("targetModified"), Number("sourceVersions"), Number("targetVersions"), Text("sourceUrl"), Text("targetUrl")),
        Table("libraries", "Libraries",
            Text("library"), Text("status"), Number("sourceFiles"), Number("targetFiles"), Number("found"), Number("sourceOnly"), Number("targetOnly"),
            Number("sizeIssues"), Number("newerAtSource"), Text("sourceUrl"), Text("targetUrl")),
    };

    private readonly record struct Slim(string Path, bool IsFolder, long Size, DateTime? Modified, int Versions);

    public override async Task RunAsync(ReportContext context)
    {
        var target = context.Target ?? throw new InvalidOperationException("This report needs a target site.");
        var excluded = ListPairing.Excluded(context, context.Options.Bool("includeSiteAssets"));
        var hidden = context.Options.Bool("includeHidden");
        var source = (await ListPairing.ListsAsync(context.Connection, hidden, context.Cancel)).Where(l => l.IsLibrary && !excluded.Contains(l.Title)).ToList();
        var targets = (await ListPairing.ListsAsync(target, hidden, context.Cancel)).Where(l => l.IsLibrary && !excluded.Contains(l.Title)).ToList();
        var sourceAuthority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        var targetAuthority = new Uri(target.SiteUrl).GetLeftPart(UriPartial.Authority);

        foreach (var (left, right) in ListPairing.Pair(source, targets))
        {
            context.Cancel.ThrowIfCancellationRequested();
            var title = (left ?? right)!.Title;
            context.Count("libraries");
            if (left is null || right is null)
            {
                // A library on one side only is one finding, not a row per file in it.
                var side = left is null ? "Target only" : "Source only";
                context.Write("libraries", new Dictionary<string, object?>
                {
                    ["library"] = title, ["status"] = side, ["sourceFiles"] = left?.Items, ["targetFiles"] = right?.Items,
                    ["sourceUrl"] = left is null ? null : sourceAuthority + left.RootFolder, ["targetUrl"] = right is null ? null : targetAuthority + right.RootFolder,
                });
                context.Count(left is null ? "librariesTargetOnly" : "librariesSourceOnly");
                continue;
            }
            context.Phase($"{title}, {left.Items:N0} items");
            try { await CompareAsync(context, target, left, right, sourceAuthority, targetAuthority); }
            catch (Exception ex) when (Gone.Is(ex)) { context.Count(Gone.Counter); }
        }
    }

    private async Task CompareAsync(ReportContext context, SpConnection target, ListPairing.ListInfo left, ListPairing.ListInfo right,
        string sourceAuthority, string targetAuthority)
    {
        var loose = context.Options.Bool("looseNames");
        var folders = context.Options.Bool("includeFolders");
        var pages = context.Options.Bool("includePages");
        var matches = context.Options.Bool("includeMatches");
        bool Wanted(FileEntry entry) =>
            entry.Name != "Forms" && (entry.IsFolder ? folders : pages || !entry.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase));
        string Key(string path) => loose ? Loosen(path) : path;

        // The source side is held in memory, keyed by the path below the library; the target side is
        // then read once and checked against it. What is left over at the end exists on the source only.
        var remaining = new Dictionary<string, Slim>(StringComparer.OrdinalIgnoreCase);
        await FileWalker.ForEachAsync(context.Connection, context.Connection.SiteUrl, left.Id, entry =>
        {
            if (!Wanted(entry)) return;
            var relative = DocumentsReport.Relative(entry.Path, left.RootFolder);
            if (relative.Length > 0) remaining[Key(relative)] = new Slim(relative, entry.IsFolder, entry.Size, entry.Modified, entry.MajorVersions);
        }, context.Cancel, onProgress: FileWalker.Progress(context, left.Title, left.Items, "source"));

        long sourceFiles = remaining.Values.LongCount(v => !v.IsFolder), targetFiles = 0, found = 0, targetOnly = 0, sizeIssues = 0, newer = 0;
        await FileWalker.ForEachAsync(target, target.SiteUrl, right.Id, entry =>
        {
            if (!Wanted(entry)) return;
            var relative = DocumentsReport.Relative(entry.Path, right.RootFolder);
            if (relative.Length == 0) return;
            if (!entry.IsFolder) targetFiles++;
            if (!remaining.Remove(Key(relative), out var there))
            {
                targetOnly++;
                Write(context, left.Title, entry.IsFolder, relative, "Target only", null, null, entry.Size, null, entry.Modified, null, entry.MajorVersions,
                    null, targetAuthority + right.RootFolder + "/" + relative);
                return;
            }
            found++;
            string status = "Found";
            string? note = null;
            if (!entry.IsFolder)
            {
                // An empty file where the source has content, or one that shrank to under a third, did not arrive whole.
                if ((there.Size > 0 && entry.Size == 0) || (there.Size > 50 * 1024 && entry.Size < there.Size * 0.3))
                {
                    status = "Size issue";
                    sizeIssues++;
                }
                // The source was changed after the copy: the target is out of date, not broken.
                if (there.Modified is { } before && entry.Modified is { } after && before > after.AddSeconds(2))
                {
                    note = "Newer at the source";
                    newer++;
                }
            }
            if (status != "Found" || note is not null || matches)
                Write(context, left.Title, entry.IsFolder, relative, status, note, there.Size, entry.Size, there.Modified, entry.Modified, there.Versions, entry.MajorVersions,
                    sourceAuthority + left.RootFolder + "/" + there.Path, targetAuthority + right.RootFolder + "/" + relative);
        }, context.Cancel, onProgress: FileWalker.Progress(context, right.Title, right.Items, "target"));

        long sourceOnly = 0, longPaths = 0;
        foreach (var missing in remaining.Values)
        {
            sourceOnly++;
            var wouldBe = targetAuthority + right.RootFolder + "/" + missing.Path;
            string? note = null;
            if (wouldBe.Length > MaxPath)
            {
                note = $"Its address on the target would be {wouldBe.Length} characters; the limit is {MaxPath}";
                longPaths++;
            }
            Write(context, left.Title, missing.IsFolder, missing.Path, "Source only", note, missing.Size, null, missing.Modified, null, missing.Versions, null,
                sourceAuthority + left.RootFolder + "/" + missing.Path, null);
        }

        context.Write("libraries", new Dictionary<string, object?>
        {
            ["library"] = left.Title,
            ["status"] = sourceOnly + targetOnly + sizeIssues == 0 ? "Match" : "Differences",
            ["sourceFiles"] = sourceFiles, ["targetFiles"] = targetFiles, ["found"] = found, ["sourceOnly"] = sourceOnly, ["targetOnly"] = targetOnly,
            ["sizeIssues"] = sizeIssues, ["newerAtSource"] = newer,
            ["sourceUrl"] = sourceAuthority + left.RootFolder, ["targetUrl"] = targetAuthority + right.RootFolder,
        });
        context.Count("sourceFiles", sourceFiles);
        context.Count("targetFiles", targetFiles);
        context.Count("found", found);
        context.Count("sourceOnly", sourceOnly);
        context.Count("targetOnly", targetOnly);
        context.Count("sizeIssues", sizeIssues);
        context.Count("newerAtSource", newer);
        context.Count("longPaths", longPaths);
    }

    private static void Write(ReportContext context, string library, bool isFolder, string path, string status, string? note,
        long? sourceSize, long? targetSize, DateTime? sourceModified, DateTime? targetModified, int? sourceVersions, int? targetVersions,
        string? sourceUrl, string? targetUrl)
    {
        context.Write("differences", new Dictionary<string, object?>
        {
            ["library"] = library,
            ["itemType"] = isFolder ? "Folder" : "File",
            ["path"] = path,
            ["status"] = status,
            ["note"] = note,
            ["sourceSizeBytes"] = isFolder ? null : sourceSize,
            ["targetSizeBytes"] = isFolder ? null : targetSize,
            ["sourceModified"] = sourceModified,
            ["targetModified"] = targetModified,
            ["sourceVersions"] = isFolder ? null : sourceVersions,
            ["targetVersions"] = isFolder ? null : targetVersions,
            ["sourceUrl"] = sourceUrl,
            ["targetUrl"] = targetUrl,
        });
    }

    /// <summary>A path with the characters migrations commonly replace turned into underscores, so both spellings compare equal.</summary>
    private static string Loosen(string path)
    {
        var loosened = new System.Text.StringBuilder(path.Length);
        foreach (var c in path)
            loosened.Append(c is '"' or '*' or ':' or '<' or '>' or '?' or '\\' or '&' or '#' or '%' or '{' or '}' or '~' or '|' ? '_' : c);
        return loosened.ToString();
    }
}
