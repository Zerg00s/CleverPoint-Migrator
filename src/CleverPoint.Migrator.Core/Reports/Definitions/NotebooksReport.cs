namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>A OneNote notebook found in a library, and whether SharePoint still treats it as one.</summary>
public sealed record FoundNotebook(string Web, string LibraryId, string Library, int ItemId, string Name, string Path, string HtmlFileType)
{
    /// <summary>What SharePoint stores on a folder it opens as a notebook.</summary>
    public const string NotebookType = "OneNote.Notebook";
    public bool Broken => !string.Equals(HtmlFileType, NotebookType, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds OneNote notebooks and tells the ones that lost their notebook marking.</summary>
public static class NotebookFinder
{
    /// <summary>
    /// The notebooks of one library. A notebook is a folder that holds a table-of-contents file
    /// (.onetoc2). Section groups inside a notebook hold one too, so only the topmost such folder
    /// counts.
    /// </summary>
    public static async Task<List<FoundNotebook>> FindAsync(Csom.SpConnection connection, string web, string libraryId, string libraryTitle, string libraryRoot,
        CancellationToken ct, Action<long>? onProgress = null)
    {
        var folders = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var withContents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await FileWalker.ForEachAsync(connection, web, libraryId, entry =>
        {
            if (entry.IsFolder) folders[entry.Path] = entry;
            else if (entry.Name.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)) withContents.Add(entry.Folder);
        }, ct, onProgress: onProgress);

        var notebooks = new List<FoundNotebook>();
        foreach (var path in withContents)
        {
            // A table of contents loose at the top of the library belongs to no folder that could be marked.
            if (path.Equals(libraryRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            // Inside another notebook: a section group, not a notebook of its own.
            if (withContents.Any(other => other.Length < path.Length && path.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (folders.TryGetValue(path, out var folder))
                notebooks.Add(new FoundNotebook(web, libraryId, libraryTitle, folder.Id, folder.Name, path, folder.HtmlFileType));
        }
        return notebooks.OrderBy(n => n.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Notebooks that a migration left behind as plain folders.</summary>
public sealed class BrokenNotebooksReport : ReportDefinition
{
    public override string Id => "broken-notebooks";
    public override string Title => "OneNote notebooks";
    public override string Group => "Discovery";
    public override string Summary => "OneNote notebooks in libraries, and which of them open as a plain folder because the notebook marking was lost, as happens after some migrations.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("brokenOnly", "bool", false, "Only list the notebooks that open as a plain folder."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("notebooks", "Notebooks", Text("webUrl"), Text("library"), Text("notebook"), Text("state"), Text("folderUrl"), Number("itemId"), Text("markedAs")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var brokenOnly = context.Options.Bool("brokenOnly");
        var authority = new Uri(context.Connection.SiteUrl).GetLeftPart(UriPartial.Authority);
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            foreach (var library in await FileWalker.LibrariesAsync(context.Connection, web, includeHidden: false, context.Cancel))
            {
                if (library.Items == 0) continue;
                context.Phase($"{library.Title}, {library.Items:N0} items");
                List<FoundNotebook> notebooks;
                try
                {
                    notebooks = await NotebookFinder.FindAsync(context.Connection, web, library.Id, library.Title, library.RootFolder, context.Cancel,
                        FileWalker.Progress(context, library.Title, library.Items));
                }
                catch (Exception ex) when (Gone.Is(ex))
                {
                    context.Count(Gone.Counter);
                    continue;
                }
                foreach (var notebook in notebooks)
                {
                    context.Count("notebooks");
                    if (notebook.Broken) context.Count("broken");
                    if (brokenOnly && !notebook.Broken) continue;
                    context.Write("notebooks", new Dictionary<string, object?>
                    {
                        ["webUrl"] = web,
                        ["library"] = notebook.Library,
                        ["notebook"] = notebook.Name,
                        ["state"] = notebook.Broken ? "Broken" : "Healthy",
                        ["folderUrl"] = authority + Links.Escape(notebook.Path),
                        ["itemId"] = notebook.ItemId,
                        ["markedAs"] = notebook.HtmlFileType,
                    });
                }
            }
        }
    }
}
