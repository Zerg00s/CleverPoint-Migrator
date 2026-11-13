using System.Text.Json;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>Every file of every library.</summary>
public sealed class DocumentsReport : ReportDefinition
{
    private const int Levels = 10;

    public override string Id => "documents";
    public override string Title => "Documents";
    public override string Group => "Inventory";
    public override string Summary => "Every file in every library: size, author, dates, version, archive state and the folder it is in. The heaviest report: a row per file.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("extensions", "list", new List<string>(), "Only files with these extensions, for example [\"pdf\",\"docx\"]. Empty means all."),
        new ReportOption("folderLevels", "bool", false, "Add ten columns with the folder at each level of the path, for pivot tables."),
        new ReportOption("includeHidden", "bool", false, "Also scan hidden libraries."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("documents", "Documents", new[]
            {
                Text("webUrl"), Text("library"), Text("folder"), Text("fileName"), Text("extension"), Number("sizeBytes"),
                Date("created"), Text("createdBy"), Date("modified"), Text("modifiedBy"), Text("version"), Number("versions"),
                IfAny(Text("archiveStatus")), IfAny(Date("archiveChanged")), Text("fileUrl"),
            }
            .Concat(Enumerable.Range(1, Levels).Select(n => IfAny(Text($"level{n}")))).ToArray()),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var extensions = context.Options.List("extensions").Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet();
        var levels = context.Options.Bool("folderLevels");
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            foreach (var library in await FileWalker.LibrariesAsync(context.Connection, web, context.Options.Bool("includeHidden"), context.Cancel))
            {
                context.Phase($"{library.Title}, {library.Items:N0} items");
                long files = 0;
                try
                {
                await FileWalker.ForEachAsync(context.Connection, web, library.Id, entry =>
                {
                    if (entry.IsFolder) return;
                    if (extensions.Count > 0 && !extensions.Contains(entry.Extension)) return;
                    var folder = Relative(entry.Folder, library.RootFolder);
                    var row = new Dictionary<string, object?>
                    {
                        ["webUrl"] = web,
                        ["library"] = library.Title,
                        ["folder"] = folder,
                        ["fileName"] = entry.Name,
                        ["extension"] = entry.Extension,
                        ["sizeBytes"] = entry.Size,
                        ["created"] = entry.Created,
                        ["createdBy"] = entry.CreatedBy,
                        ["modified"] = entry.Modified,
                        ["modifiedBy"] = entry.ModifiedBy,
                        ["version"] = entry.Version,
                        ["versions"] = entry.MajorVersions,
                        ["archiveStatus"] = ArchiveState(entry.ArchiveStatus),
                        ["archiveChanged"] = entry.ArchiveChanged,
                        ["fileUrl"] = authority + entry.Path,
                    };
                    if (levels)
                    {
                        // The library first, then each folder below it, each as the full address down to that level.
                        var path = authority + library.RootFolder;
                        row["level1"] = path;
                        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        for (var i = 0; i < parts.Length && i < Levels - 1; i++)
                        {
                            path += "/" + parts[i];
                            row[$"level{i + 2}"] = path;
                        }
                    }
                    context.Write("documents", row);
                    files++;
                    context.Count("sizeBytes", entry.Size);
                    if (entry.ArchiveStatus.Length > 0) context.Count("archivedFiles");
                }, context.Cancel, onProgress: FileWalker.Progress(context, library.Title, library.Items));
                }
                catch (Exception ex) when (Gone.Is(ex)) { context.Count(Gone.Counter); }
                context.Count("files", files);
                context.Count("libraries");
            }
        }
    }

    /// <summary>A path below its library, empty for the library's top level itself.</summary>
    internal static string Relative(string path, string libraryRoot) =>
        path.Length > libraryRoot.Length && path.StartsWith(libraryRoot.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            ? path[libraryRoot.TrimEnd('/').Length..].Trim('/')
            : "";

    internal static string? ArchiveState(string raw) => raw.ToLowerInvariant() switch
    {
        "" => null,
        "fullyarchived" => "Archived",
        "reactivating" => "Reactivating",
        _ => raw,
    };
}

/// <summary>How much storage versions take: per file, and per version when asked.</summary>
public sealed class FileVersionsReport : ReportDefinition
{
    public override string Id => "file-versions";
    public override string Title => "File versions";
    public override string Group => "Inventory";
    public override string Summary => "Storage used by the current version of each file and by its older versions. Shows where trimming versions would free space.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("versionRows", "bool", false, "Also write a row for every single version. Makes the report much larger."),
        new ReportOption("onlyWithHistory", "bool", false, "Only list files that have more than one version."),
        new ReportOption("includeHidden", "bool", false, "Also scan hidden libraries."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("files", "Files",
            Text("webUrl"), Text("library"), Text("folder"), Text("fileName"), Text("extension"), Text("version"), Number("versionCount"),
            Number("currentVersionBytes"), Number("olderVersionsBytes"), Number("allVersionsBytes"), Date("modified"), Text("modifiedBy"),
            IfAny(Text("archiveStatus")), Text("fileUrl"), IfAny(Text("note"))),
        Table("versions", "Versions",
            Text("webUrl"), Text("library"), Text("folder"), Text("fileName"), Text("versionLabel"), Flag("currentVersion"),
            Number("sizeBytes"), Date("modified"), Text("fileUrl")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var versionRows = context.Options.Bool("versionRows");
        var onlyWithHistory = context.Options.Bool("onlyWithHistory");
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            foreach (var library in await FileWalker.LibrariesAsync(context.Connection, web, context.Options.Bool("includeHidden"), context.Cancel))
            {
                context.Phase($"{library.Title}, {library.Items:N0} items");
                // A page of files at a time, so a library of any size never sits in memory.
                long read = 0;
                var progress = FileWalker.Progress(context, library.Title, library.Items);
                try
                {
                    await foreach (var page in FileWalker.PagesAsync(context.Connection, web, library.Id, context.Cancel, pageSize: 2000))
                    {
                        await WriteAsync(context, web, library.Title, library.RootFolder, authority, page.Where(e => !e.IsFolder).ToList(), versionRows, onlyWithHistory);
                        progress(read += page.Count);
                    }
                }
                catch (Exception ex) when (Gone.Is(ex)) { context.Count(Gone.Counter); }
                context.Count("libraries");
            }
        }
    }

    private static async Task WriteAsync(ReportContext context, string web, string library, string libraryRoot, string authority,
        List<FileEntry> files, bool versionRows, bool onlyWithHistory)
    {
        // Only a file that has been changed has older versions worth asking for.
        var withHistory = files.Where(f => f.Version.Length > 0 && f.Version != "1.0" && f.Version != "0.1").ToList();
        var older = new Dictionary<string, (long Bytes, int Count, List<JsonElement> Versions, string? Note)>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < withHistory.Count; i += RestBatch.MaxPerBatch)
        {
            context.Cancel.ThrowIfCancellationRequested();
            var slice = withHistory.Skip(i).Take(RestBatch.MaxPerBatch).ToList();
            var urls = slice.Select(f => VersionsUrl(web, f.Path)).ToList();
            List<(int Status, string Body)> answers;
            try
            {
                answers = await RestBatch.GetAsync(context.Connection, web, urls);
            }
            catch (SpRestException)
            {
                answers = new();      // the batch as a whole was refused: ask one by one below
            }
            for (var n = 0; n < slice.Count; n++)
            {
                var body = n < answers.Count && answers[n].Status == 200 ? answers[n].Body : null;
                if (body is null)
                {
                    try
                    {
                        using var single = await context.Connection.Rest.GetJsonAsync(urls[n]);
                        body = single.RootElement.GetRawText();
                    }
                    catch (SpRestException ex)
                    {
                        older[slice[n].Path] = (0, 0, new(), $"Versions could not be read (HTTP {ex.StatusCode}).");
                        continue;
                    }
                }
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    var versions = parsed.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                        ? value.EnumerateArray().Select(v => v.Clone()).ToList()
                        : new List<JsonElement>();
                    older[slice[n].Path] = (versions.Sum(v => v.Num("Size")), versions.Count, versionRows ? versions : new(), null);
                }
                catch (JsonException)
                {
                    older[slice[n].Path] = (0, 0, new(), "Versions could not be read.");
                }
            }
        }

        foreach (var file in files)
        {
            older.TryGetValue(file.Path, out var history);
            var hasHistory = history.Count > 0;
            context.Count("files");
            context.Count("versions", 1 + history.Count);
            context.Count("currentVersionBytes", file.Size);
            context.Count("olderVersionsBytes", history.Bytes);
            if (onlyWithHistory && !hasHistory) continue;

            var folder = DocumentsReport.Relative(file.Folder, libraryRoot);
            context.Write("files", new Dictionary<string, object?>
            {
                ["webUrl"] = web,
                ["library"] = library,
                ["folder"] = folder,
                ["fileName"] = file.Name,
                ["extension"] = file.Extension,
                ["version"] = file.Version,
                ["versionCount"] = 1 + history.Count,
                ["currentVersionBytes"] = file.Size,
                ["olderVersionsBytes"] = history.Bytes,
                ["allVersionsBytes"] = file.Size + history.Bytes,
                ["modified"] = file.Modified,
                ["modifiedBy"] = file.ModifiedBy,
                ["archiveStatus"] = DocumentsReport.ArchiveState(file.ArchiveStatus),
                ["fileUrl"] = authority + file.Path,
                ["note"] = history.Note,
            });
            if (!versionRows) continue;
            context.Write("versions", new Dictionary<string, object?>
            {
                ["webUrl"] = web, ["library"] = library, ["folder"] = folder, ["fileName"] = file.Name, ["versionLabel"] = file.Version,
                ["currentVersion"] = true, ["sizeBytes"] = file.Size, ["modified"] = file.Modified, ["fileUrl"] = authority + file.Path,
            });
            foreach (var version in history.Versions ?? new())
                context.Write("versions", new Dictionary<string, object?>
                {
                    ["webUrl"] = web, ["library"] = library, ["folder"] = folder, ["fileName"] = file.Name, ["versionLabel"] = version.Str("VersionLabel"),
                    ["sizeBytes"] = version.Num("Size"), ["modified"] = version.When("Created"), ["fileUrl"] = authority + file.Path,
                });
        }
    }

    /// <summary>The older versions of a file. Addressed by decoded path, which is the form that copes with # and % in names.</summary>
    private static string VersionsUrl(string web, string serverRelativePath) =>
        $"{web}/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePath.Replace("'", "''"))}')/versions" +
        "?$select=VersionLabel,Size,Created,IsCurrentVersion&$top=5000";
}
