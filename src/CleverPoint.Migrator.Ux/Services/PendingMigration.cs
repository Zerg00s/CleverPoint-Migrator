namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Carries what the explorer picked over to the wizard. Selections (file/folder
/// paths or item IDs) can be large, so they travel through this singleton rather
/// than the navigation query string.
/// </summary>
public class PendingMigration
{
    public string SourceSite { get; set; } = "";
    public string TargetSite { get; set; } = "";
    public List<PendingJob> Jobs { get; } = new();

    /// <summary>When set, copy into this existing target list (content-only) instead
    /// of creating one named after the source. Used when dropping/copying a specific
    /// selection into an already-open target library.</summary>
    public string? TargetListTitle { get; set; }
    public string? TargetListUrl { get; set; }
    public bool ContentOnly { get; set; }

    public void Reset(string source, string target)
    {
        SourceSite = source;
        TargetSite = target;
        Jobs.Clear();
        TargetListTitle = null;
        TargetListUrl = null;
        ContentOnly = false;
    }
}

/// <summary>One list/library to copy, with an optional surgical selection.</summary>
public class PendingJob
{
    public string SourceListTitle { get; set; } = "";
    public bool IsLibrary { get; set; }

    /// <summary>Library file/folder server-relative URLs to copy. Empty = whole library.</summary>
    public List<string> SelectedPaths { get; set; } = new();

    /// <summary>Generic-list item IDs to copy. Empty = whole list.</summary>
    public List<int> SelectedItemIds { get; set; } = new();

    /// <summary>Folder vs file/item split of the selection (for the task name).</summary>
    public int SelectedFolderCount { get; set; }
    public int SelectedFileCount { get; set; }

    public bool WholeList => SelectedPaths.Count == 0 && SelectedItemIds.Count == 0;
}
