namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Remembers each explorer pane's location and selection so navigating away
/// (e.g. to the copy task page) and back restores exactly where you were -
/// connection, subsite chain, open list, folder, and ticked items.
/// </summary>
public class ExplorerState
{
    // Static so the snapshot survives no matter how the component tree or DI scope
    // is rebuilt across navigations (a plain singleton proved not to be shared
    // between the disposed pane and its recreated replacement here).
    public PaneSnapshot? Source { get => _source; set => _source = value; }
    public PaneSnapshot? Target { get => _target; set => _target = value; }

    private static PaneSnapshot? _source;
    private static PaneSnapshot? _target;
}

/// <summary>Source scope persisted with a run (as JSON in MigrationRun.ScopeJson)
/// so the run can be re-opened in the explorer at the exact folder + selection.</summary>
public class RunScope
{
    public string? SourceFolder { get; set; }
    public string? TargetListUrl { get; set; }
    public string? TargetSubfolder { get; set; }
    public List<string> SelectedPaths { get; set; } = new();
    public List<int> ItemIds { get; set; } = new();
}

public class PaneSnapshot
{
    public string Site { get; set; } = "";
    public List<(string Title, string Url)> WebCrumbs { get; set; } = new();
    public string OpenListServerRelativeUrl { get; set; } = "";
    /// <summary>Used to re-open a list when only its title is known (history restore).</summary>
    public string OpenListTitle { get; set; } = "";
    /// <summary>Subfolder to open relative to the list root (history restore, when the
    /// absolute FolderUrl isn't known until the list loads).</summary>
    public string OpenSubfolderRelative { get; set; } = "";
    public bool OpenListIsLibrary { get; set; }
    public List<(string Name, string Url)> FolderCrumbs { get; set; } = new();
    public string FolderUrl { get; set; } = "";
    public HashSet<string> PickedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PickedFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> PickedIds { get; set; } = new();
    public HashSet<string> TickedListTitles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
