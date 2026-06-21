namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Shared state for dragging files/folders from the source explorer onto the
/// target pane. The payload lives here (not in the browser dataTransfer) because
/// both panes are in the same app and need the picked paths/IDs on drop.
/// </summary>
public class DragState
{
    public bool Active { get; private set; }
    public string SourceSite { get; private set; } = "";
    public string SourceListTitle { get; private set; } = "";
    public bool IsLibrary { get; private set; }
    public List<string> Paths { get; private set; } = new();
    public List<int> ItemIds { get; private set; } = new();

    /// <summary>Items being dragged (for the drop-zone hint text).</summary>
    public int Count => IsLibrary ? Paths.Count : ItemIds.Count;

    public event Action? Changed;

    public void Begin(string site, string list, bool isLibrary, List<string> paths, List<int> ids)
    {
        SourceSite = site;
        SourceListTitle = list;
        IsLibrary = isLibrary;
        Paths = paths;
        ItemIds = ids;
        Active = true;
        Changed?.Invoke();
    }

    public void End()
    {
        Active = false;
        Changed?.Invoke();
    }
}
