namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>
/// Where the storage goes: per folder, per file type, and the largest files, each split into what the
/// current versions take and what the older versions take. This is the data behind the storage map.
///
/// It costs one read of each library and nothing more: SharePoint returns, with every file, both its
/// current size and its total with all versions. Folder totals are added up here from the files, so
/// the numbers of a folder, its library and its site always agree with each other.
/// </summary>
public sealed class StorageReport : ReportDefinition
{
    public override string Id => "storage";
    public override string Title => "Storage";
    public override string Group => "Inventory";
    public override string Summary => "Where the storage goes: every folder with what it holds, split into current and older versions, plus totals per kind of file (PDF, Office, video, images) and file type, the largest files, and each library's version limits. The data behind the storage map.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("largestFiles", "int", 500, "The largest files of each site are listed one by one and get a box of their own on the map; this is how many. All other files are still counted in their folders."),
        new ReportOption("includeHidden", "bool", true, "Also scan hidden libraries, such as the Preservation Hold Library, which can be large."),
        new ReportOption("recycleBin", "bool", true, "Measure the recycle bin, which counts toward the site's storage."),
        new ReportOption("includeSubsites", "bool", true, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("folders", "Folders",
            Text("webUrl"), Text("library"), Text("path"), Number("depth"), Number("files"), Number("totalBytes"), Number("currentBytes"),
            Number("versionBytes"), Number("versionPercent"), Number("ownFiles"), Number("ownBytes"), IfAny(Text("versionLimits")), Text("folderUrl")) with { SortBy = "totalBytes" },
        Table("types", "File types",
            Text("kind"), Text("extension"), Number("files"), Number("totalBytes"), Number("currentBytes"), Number("versionBytes"), Number("versionPercent")) with { SortBy = "totalBytes" },
        // The files directly in each folder, by kind. Where the videos are, where the PDFs are.
        Table("folderKinds", "Kinds by folder",
            Text("webUrl"), Text("library"), Text("path"), Text("kind"), Number("files"), Number("totalBytes"), Number("currentBytes"), Number("versionBytes"),
            Text("folderUrl")) with { SortBy = "totalBytes" },
        // Lists are not scanned item by item: SharePoint says what each one takes, attachments included, in one call per site.
        Table("lists", "Lists and attachments",
            Text("webUrl"), Text("list"), Number("items"), Number("attachments"), Number("totalBytes"), IfAny(Flag("hidden")), Text("listUrl")) with { SortBy = "totalBytes" },
        Table("overview", "Overview",
            Number("usedBytes"), Number("libraryBytes"), Number("currentBytes"), Number("versionBytes"), Number("recycleBinBytes"), Number("recycleBinItems"),
            Number("otherBytes"), IfAny(Number("listBytes")), IfAny(Number("preservationHoldBytes")), IfAny(Number("archivedFiles")), IfAny(Number("archivedBytes")), IfAny(Text("tenantVersionLimits")), IfAny(Text("siteVersionLimits")), IfAny(Number("librariesKeepingVersionsForever")),
            IfAny(Number("keptForeverVersionBytes")), IfAny(Text("note"))),
        Table("files", "Largest files",
            Text("webUrl"), Text("library"), Text("folder"), Text("fileName"), Text("extension"), Number("totalBytes"), Number("currentBytes"),
            Number("versionBytes"), Text("version"), Date("modified"), Text("modifiedBy"), IfAny(Text("archiveStatus")), Text("fileUrl")) with { SortBy = "totalBytes" },
    };

    /// <summary>The files directly in a folder, and after the roll-up everything below it too.</summary>
    private sealed class Sum
    {
        public long OwnFiles, OwnTotal, OwnCurrent, OwnVersions;
        public long Files, Total, Current, Versions;
        /// <summary>The folder's own files by kind: for each kind in <see cref="FileKinds.All"/> its files, total, current and version bytes. Null until the folder has a file.</summary>
        public long[]? Kinds;
    }

    private sealed record Big(string Web, string Library, string Folder, FileEntry File);

    public override async Task RunAsync(ReportContext context)
    {
        var keep = Math.Clamp(context.Options.Int("largestFiles"), 0, 100_000);
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        var types = new Dictionary<string, Sum>(StringComparer.OrdinalIgnoreCase);
        // The largest files of the site so far, smallest of them first so it is the one to drop.
        var largest = new PriorityQueue<Big, long>();
        long siteTotal = 0, siteCurrent = 0, siteVersions = 0;
        // Libraries whose limits never delete a version for its age, and what their older versions take.
        long foreverLibraries = 0, foreverBytes = 0;
        // What a retention policy or a hold keeps: copies of changed and deleted files go to a hidden
        // library of their own, the Preservation Hold Library. Null when hidden libraries are not scanned.
        long? holdBytes = context.Options.Bool("includeHidden") ? 0 : null;
        // Files moved to Microsoft 365 Archive one by one. They stay in their library and still count
        // toward its size here; the tenant's own "archive storage" figure only covers whole archived sites.
        long archivedFiles = 0, archivedBytes = 0;
        long? listBytes = null;

        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            var limits = (await VersionLimits.LibrariesAsync(context.Connection, web, context.Cancel)).ToDictionary(l => l.Id, l => l.Limits, StringComparer.OrdinalIgnoreCase);
            if (await ListsAsync(context, web, authority) is { } inLists) listBytes = (listBytes ?? 0) + inLists;
            foreach (var library in await FileWalker.LibrariesAsync(context.Connection, web, context.Options.Bool("includeHidden"), context.Cancel))
            {
                context.Phase($"{library.Title}, {library.Items:N0} items");
                var root = library.RootFolder.TrimEnd('/');
                // Keyed by path below the library; "" is the library itself.
                var folders = new Dictionary<string, Sum>(StringComparer.OrdinalIgnoreCase) { [""] = new Sum() };
                try
                {
                    await FileWalker.ForEachAsync(context.Connection, web, library.Id, entry =>
                    {
                        if (entry.IsFolder)
                        {
                            var path = DocumentsReport.Relative(entry.Path, root);
                            if (path.Length > 0 && !path.Equals("Forms", StringComparison.OrdinalIgnoreCase)) folders.TryAdd(path, new Sum());
                            return;
                        }
                        var folder = DocumentsReport.Relative(entry.Folder, root);
                        if (!folders.TryGetValue(folder, out var sum)) folders[folder] = sum = new Sum();
                        var total = entry.StorageBytes;
                        var versions = entry.VersionBytes;
                        var current = entry.StreamSize > 0 ? entry.StreamSize : entry.Size;
                        sum.OwnFiles++;
                        sum.OwnTotal += total;
                        sum.OwnCurrent += current;
                        sum.OwnVersions += versions;
                        if (entry.ArchiveStatus.Length > 0)
                        {
                            archivedFiles++;
                            archivedBytes += total;
                        }
                        var slot = FileKinds.IndexOf(FileKinds.Of(entry.Extension)) * 4;
                        sum.Kinds ??= new long[FileKinds.All.Count * 4];
                        sum.Kinds[slot]++;
                        sum.Kinds[slot + 1] += total;
                        sum.Kinds[slot + 2] += current;
                        sum.Kinds[slot + 3] += versions;

                        var extension = entry.Extension.Length > 0 ? entry.Extension : "(none)";
                        if (!types.TryGetValue(extension, out var type)) types[extension] = type = new Sum();
                        type.Files++;
                        type.Total += total;
                        type.Current += current;
                        type.Versions += versions;

                        if (keep == 0) return;
                        if (largest.Count < keep) largest.Enqueue(new Big(web, library.Title, folder, entry), total);
                        else if (largest.TryPeek(out _, out var smallest) && total > smallest)
                        {
                            largest.Dequeue();
                            largest.Enqueue(new Big(web, library.Title, folder, entry), total);
                        }
                    }, context.Cancel, onProgress: FileWalker.Progress(context, library.Title, library.Items));
                }
                catch (Exception ex) when (Gone.Is(ex))
                {
                    context.Count(Gone.Counter);
                    continue;
                }

                // Every folder passes its totals up to its parent, deepest first. Folders that only show
                // up as part of a file's path are added on the way.
                foreach (var path in folders.Keys.ToList())
                    for (var parent = Parent(path); parent is not null && folders.TryAdd(parent, new Sum()); parent = Parent(parent)) { }
                foreach (var sum in folders.Values)
                {
                    sum.Files = sum.OwnFiles;
                    sum.Total = sum.OwnTotal;
                    sum.Current = sum.OwnCurrent;
                    sum.Versions = sum.OwnVersions;
                }
                foreach (var path in folders.Keys.Where(p => p.Length > 0).OrderByDescending(Depth))
                {
                    var sum = folders[path];
                    var parent = folders[Parent(path)!];
                    parent.Files += sum.Files;
                    parent.Total += sum.Total;
                    parent.Current += sum.Current;
                    parent.Versions += sum.Versions;
                }

                foreach (var (path, sum) in folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    context.Write("folders", new Dictionary<string, object?>
                    {
                        ["webUrl"] = web,
                        ["library"] = library.Title,
                        ["path"] = path,
                        ["depth"] = Depth(path),
                        ["files"] = sum.Files,
                        ["totalBytes"] = sum.Total,
                        ["currentBytes"] = sum.Current,
                        ["versionBytes"] = sum.Versions,
                        ["versionPercent"] = Percent(sum.Versions, sum.Total),
                        ["ownFiles"] = sum.OwnFiles,
                        ["ownBytes"] = sum.OwnTotal,
                        ["versionLimits"] = path.Length == 0 ? limits.GetValueOrDefault(library.Id) : null,
                        ["folderUrl"] = authority + root + (path.Length > 0 ? "/" + path : ""),
                    });

                foreach (var (path, sum) in folders.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (sum.Kinds is null) continue;
                    for (var kind = 0; kind < FileKinds.All.Count; kind++)
                    {
                        if (sum.Kinds[kind * 4] == 0) continue;
                        context.Write("folderKinds", new Dictionary<string, object?>
                        {
                            ["webUrl"] = web,
                            ["library"] = library.Title,
                            ["path"] = path,
                            ["kind"] = FileKinds.All[kind],
                            ["files"] = sum.Kinds[kind * 4],
                            ["totalBytes"] = sum.Kinds[kind * 4 + 1],
                            ["currentBytes"] = sum.Kinds[kind * 4 + 2],
                            ["versionBytes"] = sum.Kinds[kind * 4 + 3],
                            ["folderUrl"] = authority + root + (path.Length > 0 ? "/" + path : ""),
                        });
                    }
                }

                var whole = folders[""];
                if (holdBytes is not null && (library.Template == 1310 || root.EndsWith("/PreservationHoldLibrary", StringComparison.OrdinalIgnoreCase)))
                    holdBytes += whole.Total;
                if (limits.GetValueOrDefault(library.Id) is { } libraryLimits && VersionLimits.KeepsForever(libraryLimits))
                {
                    foreverLibraries++;
                    foreverBytes += whole.Versions;
                }
                context.Count("libraries");
                context.Count("folders", folders.Count - 1);
                context.Count("files", whole.Files);
                context.Count("totalBytes", whole.Total);
                context.Count("currentBytes", whole.Current);
                context.Count("versionBytes", whole.Versions);
                siteTotal += whole.Total;
                siteCurrent += whole.Current;
                siteVersions += whole.Versions;
            }
        }

        await OverviewAsync(context, siteTotal, siteCurrent, siteVersions, foreverLibraries, foreverBytes, holdBytes, archivedFiles, archivedBytes, listBytes);

        foreach (var (extension, sum) in types.OrderByDescending(t => t.Value.Total))
            context.Write("types", new Dictionary<string, object?>
            {
                ["kind"] = FileKinds.Of(extension),
                ["extension"] = extension,
                ["files"] = sum.Files,
                ["totalBytes"] = sum.Total,
                ["currentBytes"] = sum.Current,
                ["versionBytes"] = sum.Versions,
                ["versionPercent"] = Percent(sum.Versions, sum.Total),
            });

        foreach (var big in largest.UnorderedItems.Select(i => i.Element).OrderByDescending(b => b.File.StorageBytes))
            context.Write("files", new Dictionary<string, object?>
            {
                ["webUrl"] = big.Web,
                ["library"] = big.Library,
                ["folder"] = big.Folder,
                ["fileName"] = big.File.Name,
                ["extension"] = big.File.Extension,
                ["totalBytes"] = big.File.StorageBytes,
                ["currentBytes"] = big.File.StreamSize > 0 ? big.File.StreamSize : big.File.Size,
                ["versionBytes"] = big.File.VersionBytes,
                ["version"] = big.File.Version,
                ["modified"] = big.File.Modified,
                ["modifiedBy"] = big.File.ModifiedBy,
                ["archiveStatus"] = DocumentsReport.ArchiveState(big.File.ArchiveStatus),
                ["fileUrl"] = authority + big.File.Path,
            });
    }

    /// <summary>
    /// What SharePoint itself says the site uses, next to what was found in its libraries. The
    /// difference is storage a library scan does not see: the recycle bin, lists and their
    /// attachments, and what SharePoint keeps for itself.
    /// </summary>
    private static async Task OverviewAsync(ReportContext context, long libraries, long current, long versions, long foreverLibraries, long foreverBytes, long? holdBytes,
        long archivedFiles, long archivedBytes, long? listBytes)
    {
        if (listBytes is { } inLists) context.Count("listBytes", inLists);
        if (holdBytes is { } held) context.Count("preservationHoldBytes", held);
        context.Count("archivedFiles", archivedFiles);
        context.Count("archivedBytes", archivedBytes);
        var site = context.Connection.SiteUrl;
        var tenantLimits = await VersionLimits.TenantAsync(context.Connect, site);
        var siteLimits = await VersionLimits.SiteAsync(context.Connection, site);
        context.Count("librariesKeepingVersionsForever", foreverLibraries);
        context.Count("keptForeverVersionBytes", foreverBytes);
        long used = 0, binBytes = 0, binItems = 0;
        string? note = null;
        // The storage figure and the recycle bin belong to the site collection. When what was scanned
        // is a subsite, they would be the numbers of something larger than the scan.
        var wholeCollection = true;
        try
        {
            using var collection = await context.Connection.Rest.GetJsonAsync($"{site}/_api/site?$select=Url");
            wholeCollection = collection.RootElement.Str("Url").TrimEnd('/').Equals(site.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Http.SpRestException) { /* treated as a site collection; the calls below say if it is not readable */ }
        if (!wholeCollection)
        {
            context.Write("overview", new Dictionary<string, object?>
            {
                ["libraryBytes"] = libraries, ["currentBytes"] = current, ["versionBytes"] = versions,
                ["listBytes"] = listBytes, ["preservationHoldBytes"] = holdBytes, ["archivedFiles"] = archivedFiles, ["archivedBytes"] = archivedBytes,
                ["tenantVersionLimits"] = tenantLimits, ["siteVersionLimits"] = siteLimits.Length > 0 ? siteLimits : null,
                ["librariesKeepingVersionsForever"] = foreverLibraries, ["keptForeverVersionBytes"] = foreverBytes,
                ["note"] = "A subsite was scanned. The storage figure and the recycle bin belong to its site collection and are left out.",
            });
            return;
        }
        try
        {
            using var usage = await context.Connection.Rest.GetJsonAsync($"{site}/_api/site/usage");
            used = usage.RootElement.Num("Storage");
        }
        catch (Http.SpRestException) { note = "The site's own storage figure could not be read."; }

        if (context.Options.Bool("recycleBin"))
        {
            // Both stages of the recycle bin. A very full bin is counted up to a limit, so the report does not spend longer on the bin than on the site.
            const long limit = 500_000;
            try
            {
                await context.Connection.Rest.ForEachAsync($"{site}/_api/site/RecycleBin?$select=Size&$top=5000", item =>
                {
                    binItems++;
                    binBytes += item.Num("Size");
                    if (binItems % 5000 == 0) context.Phase($"Recycle bin, {binItems:N0} items");
                    if (binItems >= limit) throw new OperationCanceledException("limit");
                }, context.Cancel);
            }
            catch (OperationCanceledException) when (!context.Cancel.IsCancellationRequested)
            {
                note = $"The recycle bin holds more than {limit:N0} items; only that many were measured.";
            }
            catch (Http.SpRestException)
            {
                note = "The recycle bin could not be read with this connection.";
            }
        }

        context.Write("overview", new Dictionary<string, object?>
        {
            ["usedBytes"] = used > 0 ? used : null,
            ["libraryBytes"] = libraries,
            ["currentBytes"] = current,
            ["versionBytes"] = versions,
            ["recycleBinBytes"] = binBytes,
            ["recycleBinItems"] = binItems,
            ["otherBytes"] = used > 0 ? Math.Max(0, used - libraries - binBytes) : null,
            ["listBytes"] = listBytes,
            ["preservationHoldBytes"] = holdBytes,
            ["archivedFiles"] = archivedFiles,
            ["archivedBytes"] = archivedBytes,
            ["tenantVersionLimits"] = tenantLimits,
            ["siteVersionLimits"] = siteLimits.Length > 0 ? siteLimits : null,
            ["librariesKeepingVersionsForever"] = foreverLibraries,
            ["keptForeverVersionBytes"] = foreverBytes,
            ["note"] = note,
        });
        context.Count("siteUsedBytes", used);
        context.Count("recycleBinBytes", binBytes);
    }

    /// <summary>A list smaller than this is counted but gets no row: every site has dozens of tiny system lists.</summary>
    private const long SmallestListListed = 1024 * 1024;

    /// <summary>
    /// What the lists of a web take, attachments included, as SharePoint accounts it. Lists of a megabyte
    /// or more get a row each. Null when the web does not answer.
    /// </summary>
    private static async Task<long?> ListsAsync(ReportContext context, string web, string authority)
    {
        try
        {
            long total = 0;
            var lists = await context.Connection.Rest.GetAllAsync(
                $"{web}/_api/web/lists?$select=Title,ItemCount,Hidden,RootFolder/ServerRelativeUrl,RootFolder/StorageMetrics/TotalSize,RootFolder/StorageMetrics/TotalFileCount" +
                "&$expand=RootFolder,RootFolder/StorageMetrics&$filter=BaseType eq 0&$top=500", context.Cancel);
            foreach (var list in lists)
            {
                var metrics = list.Child("RootFolder").Child("StorageMetrics");
                // SharePoint sends these two numbers as text.
                long.TryParse(metrics.Str("TotalSize"), out var bytes);
                long.TryParse(metrics.Str("TotalFileCount"), out var attachments);
                total += bytes;
                if (bytes < SmallestListListed) continue;
                context.Write("lists", new Dictionary<string, object?>
                {
                    ["webUrl"] = web,
                    ["list"] = list.Str("Title"),
                    ["items"] = list.Num("ItemCount"),
                    ["attachments"] = attachments,
                    ["totalBytes"] = bytes,
                    ["hidden"] = list.Bool("Hidden") ? true : null,
                    ["listUrl"] = authority + list.Child("RootFolder").Str("ServerRelativeUrl"),
                });
            }
            return total;
        }
        catch (Http.SpRestException)
        {
            return null;
        }
    }

    /// <summary>The folder a path is in: "" for a top-level folder, null for the library itself.</summary>
    private static string? Parent(string path) => path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    private static double Percent(long part, long whole) => whole > 0 ? Math.Round(part * 100.0 / whole, 1) : 0;
}
