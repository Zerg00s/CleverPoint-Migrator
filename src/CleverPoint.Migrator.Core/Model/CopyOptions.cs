namespace CleverPoint.Migrator.Core.Model;

/// <summary>What to do when the item already exists on the target.</summary>
public enum ExistingItemMode { Overwrite, CopyIfNewer, Skip }

/// <summary>Which date a date filter applies to.</summary>
public enum DateFilterField { Modified, Created }

/// <summary>
/// Options for one list/library copy operation. Defaults follow the
/// "preserve everything, touch nothing extra" philosophy.
/// </summary>
public class CopyOptions
{
    /// <summary>What to do when the target item already exists.</summary>
    public ExistingItemMode ExistingMode { get; set; } = ExistingItemMode.Overwrite;

    /// <summary>Which date the Since/Before filter applies to.</summary>
    public DateFilterField DateField { get; set; } = DateFilterField.Modified;
    /// <summary>Title for the target list. When the target list does not exist it is created.</summary>
    public string TargetListTitle { get; set; } = "";

    /// <summary>URL leaf for a newly created target list (e.g. "Lists/MyCopy" or "MyCopyLib").</summary>
    public string? TargetListUrl { get; set; }

    /// <summary>Optional subfolder INSIDE the target list to copy into (relative to the
    /// list root, e.g. "Archive/2024"). Empty/null copies into the list root. Used when
    /// the user drops onto a specific folder in the target pane.</summary>
    public string? TargetSubfolderRelative { get; set; }

    public bool CopyViews { get; set; } = true;
    public bool CopyListSettings { get; set; } = true;

    /// <summary>
    /// When false (content-only copies), the target schema is NOT touched in
    /// any way: no field creation, no formatting sync, no lookup rewiring.
    /// The target list must already exist.
    /// </summary>
    public bool MergeSchema { get; set; } = true;

    /// <summary>Preserve Created/Modified/Author/Editor on copied items and files.</summary>
    public bool PreserveAuthorsAndDates { get; set; } = true;

    /// <summary>Copy item/file/folder role assignments (permissions).</summary>
    public bool CopyPermissions { get; set; }

    /// <summary>
    /// How many files to transfer concurrently within one library copy. 1 = sequential (the
    /// safe default). Higher values copy files in parallel through per-worker connections,
    /// sharing the throttle budget. Forced to 1 when CopyPermissions is on (permission
    /// application is stateful). Folders are always created sequentially first.
    /// </summary>
    public int ParallelFileTransfers { get; set; } = 1;

    /// <summary>Login/email used when a source user cannot be resolved on the target.</summary>
    public string? UnresolvedUserFallback { get; set; }

    /// <summary>
    /// Optional OVERRIDE for the managed-metadata term remap (source term GUID -> target term GUID).
    /// Leave null: TermStoreCopier replicates the term sets into the target store and derives this
    /// map itself (identity same-tenant, and cross-tenant too whenever it can preserve the GUIDs).
    /// Set it only to force specific terms onto pre-existing target terms.
    /// </summary>
    public Dictionary<Guid, Guid>? TermMap { get; set; }

    /// <summary>Only copy items modified on/after this UTC time (delta and filter support).</summary>
    public DateTime? ModifiedSinceUtc { get; set; }

    /// <summary>Only copy items modified before this UTC time.</summary>
    public DateTime? ModifiedBeforeUtc { get; set; }

    /// <summary>
    /// Optional server-relative folder inside the source list to copy from (subset copy). Null = whole list.
    ///
    /// This means "copy what is INSIDE this folder": its contents land directly under the target root /
    /// TargetSubfolderRelative, WITHOUT recreating the source's intermediate folders. Copying
    /// "Lib/Folder-A/Sub-1" into "Target/Subfolder" puts Sub-1's files in "Target/Subfolder", not in
    /// "Target/Subfolder/Folder-A/Sub-1". Ignored when SelectedPaths names an explicit selection, which
    /// keeps its own list-root-relative layout.
    /// </summary>
    public string? SourceFolderServerRelativeUrl { get; set; }

    /// <summary>
    /// The base the copied paths are relative to: the scoped source folder for a folder copy, else the
    /// list root. Shared by the item and file copiers so both lay content out the same way.
    /// </summary>
    public string PathBase(string listRootServerRelativeUrl) =>
        !string.IsNullOrEmpty(SourceFolderServerRelativeUrl) && SelectedPaths.Count == 0
            ? SourceFolderServerRelativeUrl.TrimEnd('/')
            : listRootServerRelativeUrl;

    /// <summary>
    /// Optional wildcard patterns on the item/file NAME (leaf), e.g. "*.pdf"
    /// or "Invoice*". An item copies when it matches ANY pattern. Folders
    /// always copy (children may match).
    /// </summary>
    public List<string> NamePatterns { get; set; } = new();

    /// <summary>
    /// Optional explicit source item IDs to copy (explorer selection on a
    /// generic list). Empty = no ID filter.
    /// </summary>
    public List<int> ItemIds { get; set; } = new();

    /// <summary>
    /// Optional surgical selection: server-relative paths of files AND/OR
    /// folders picked in the explorer. A file copies when its path is listed;
    /// a folder copies with everything underneath it. Parents of selected
    /// paths are recreated on demand. Empty = no path filter.
    /// </summary>
    public List<string> SelectedPaths { get; set; } = new();

    /// <summary>
    /// Optional field mapping: source internal column name -> target internal
    /// column name. Values write to the mapped target column.
    /// </summary>
    public Dictionary<string, string> FieldMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int PageSize { get; set; } = 200;

    /// <summary>
    /// Migration API engine: max items (files + folders) per migration job.
    /// Microsoft's guidance is to keep packages under ~250 items; large
    /// libraries are split into a pipeline of jobs automatically.
    /// </summary>
    public int ApiMaxItemsPerPackage { get; set; } = 200;

    /// <summary>
    /// Files at/above this size copy through the streaming chunked-upload
    /// path (REST upload sessions, O(slice) memory, works to SPO's 250 GB
    /// limit). In the Migration API engine these files bypass the package
    /// (the API caps files at 15 GB) and go hybrid through the same path.
    /// </summary>
    public long LargeFileThresholdBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Slice size for chunked upload sessions.</summary>
    public int UploadSliceBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Source paths (FileRefs) to skip because an earlier interrupted run
    /// already copied them. Populate from HistoryStore.GetCopiedSourcePaths.
    /// </summary>
    public HashSet<string>? ResumeSkipPaths { get; set; }

    /// <summary>Record items filtered out by delta/date filters as Skipped (visible in the log).</summary>
    public bool RecordSkippedItems { get; set; } = true;

    /// <summary>
    /// Source item id -> target item id map from previous runs (HistoryStore).
    /// Delta runs UPDATE these targets instead of creating duplicates.
    /// </summary>
    public Dictionary<int, int>? UpsertItemMap { get; set; }

    /// <summary>Copy list item attachments (downloads + re-applies preserved dates afterwards).</summary>
    public bool CopyAttachments { get; set; } = true;

    /// <summary>When false, only schema (fields, views, settings) is copied; content is skipped.</summary>
    public bool CopyContent { get; set; } = true;

    /// <summary>How many versions to migrate per file (1 = latest only; higher counts need the Migration API version support).</summary>
    public int MaxVersions { get; set; } = 1;

    /// <summary>Items per CSOM batch (one ExecuteQuery round trip).</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// A full copy of these options. Used for healing/retry so nothing is silently
    /// dropped (target subfolder, field map, schema mode, version depth, upsert map,
    /// filters, etc.). Collections are copied so the clone can be mutated independently.
    /// </summary>
    public CopyOptions Clone() => new()
    {
        ExistingMode = ExistingMode,
        DateField = DateField,
        TargetListTitle = TargetListTitle,
        TargetListUrl = TargetListUrl,
        TargetSubfolderRelative = TargetSubfolderRelative,
        CopyViews = CopyViews,
        CopyListSettings = CopyListSettings,
        MergeSchema = MergeSchema,
        PreserveAuthorsAndDates = PreserveAuthorsAndDates,
        CopyPermissions = CopyPermissions,
        ParallelFileTransfers = ParallelFileTransfers,
        UnresolvedUserFallback = UnresolvedUserFallback,
        TermMap = TermMap == null ? null : new Dictionary<Guid, Guid>(TermMap),
        ModifiedSinceUtc = ModifiedSinceUtc,
        ModifiedBeforeUtc = ModifiedBeforeUtc,
        SourceFolderServerRelativeUrl = SourceFolderServerRelativeUrl,
        NamePatterns = new List<string>(NamePatterns),
        ItemIds = new List<int>(ItemIds),
        SelectedPaths = new List<string>(SelectedPaths),
        FieldMap = new Dictionary<string, string>(FieldMap, StringComparer.OrdinalIgnoreCase),
        PageSize = PageSize,
        ApiMaxItemsPerPackage = ApiMaxItemsPerPackage,
        LargeFileThresholdBytes = LargeFileThresholdBytes,
        UploadSliceBytes = UploadSliceBytes,
        ResumeSkipPaths = ResumeSkipPaths == null ? null : new HashSet<string>(ResumeSkipPaths, StringComparer.OrdinalIgnoreCase),
        RecordSkippedItems = RecordSkippedItems,
        UpsertItemMap = UpsertItemMap == null ? null : new Dictionary<int, int>(UpsertItemMap),
        CopyAttachments = CopyAttachments,
        CopyContent = CopyContent,
        MaxVersions = MaxVersions,
        BatchSize = BatchSize,
    };
}
