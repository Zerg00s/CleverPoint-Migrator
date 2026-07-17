using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Taxonomy;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Copies list items (including folder structure) from a source list to a
/// target list, preserving Created/Modified/Author/Editor and field values.
///
/// Metadata preservation patterns (verified against real tenants, see the
/// project's PnP knowledge base):
///  - items and documents: set fields + Author/Editor/Created/Modified, then
///    UpdateOverwriteVersion(). SystemUpdate silently fails on documents in
///    some tenants, so it is never used here.
///  - folders: user fields are rejected by UpdateOverwriteVersion, so they go
///    through ValidateUpdateListItem (claims-key JSON) FIRST, then dates via
///    UpdateOverwriteVersion LAST (ValidateUpdateListItem stamps Modified).
/// </summary>
public class ItemCopier
{
    private static readonly HashSet<string> NeverCopyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID", "GUID", "UniqueId", "FileRef", "FileDirRef", "FileLeafRef", "Attachments",
        "ContentTypeId", "ContentType", "_UIVersionString", "owshiddenversion", "FSObjType",
        "Created_x0020_Date", "Last_x0020_Modified", "ItemChildCount", "FolderChildCount",
        "AppAuthor", "AppEditor", "ComplianceAssetId", "_ComplianceFlags", "_ComplianceTag",
        "_ComplianceTagWrittenTime", "_ComplianceTagUserId", "SortBehavior", "PermMask",
        "PrincipalCount", "Restricted", "OriginatorId", "NoExecute", "ContentVersion",
        "AccessPolicy", "MetaInfo", "_Level", "_IsCurrentVersion", "_ModerationStatus",
        "InstanceID", "Order", "WorkflowVersion", "WorkflowInstanceID", "ParentVersionString",
        "ParentLeafName", "DocConcurrencyNumber", "Edit", "LinkTitleNoMenu", "LinkTitle",
        "LinkTitle2", "DocIcon", "ServerUrl", "EncodedAbsUrl", "BaseName", "FileSizeDisplay",
        "SelectTitle", "SyncClientId", "ProgId", "ScopeId", "VirusStatus", "CheckedOutUserId",
        "CheckedOutTitle", "LinkFilenameNoMenu", "LinkFilename", "LinkFilename2",
        "ParentUniqueId", "StreamHash", "Combine", "RepairDocument", "A2ODMountCount",
        "_HasCopyDestinations", "_CopySource", "_EditMenuTableStart", "_EditMenuTableStart2",
        "_EditMenuTableEnd", "_VirusStatus", "_VirusVendorID", "_VirusInfo", "_CommentFlags",
        "_CommentCount", "_LikeCount", "_DisplayName", "Author", "Editor", "Created", "Modified",
        "SMLastModifiedDate", "SMTotalSize", "SMTotalFileStreamSize", "SMTotalFileCount",
        "_RmsTemplateId", "_IpLabelId", "_IpLabelAssignmentMethod", "_IpLabelPromotionCtagVersion",
        "MediaServiceImageTags", "MediaServiceOCR", "MediaServiceLocation",
    };

    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;
    private readonly UserResolver _users;

    /// <summary>
    /// Per lookup field: source item id -> target item id, or null for
    /// identity (same-web copies where the field points at the same list).
    /// </summary>
    public Dictionary<string, Dictionary<int, int>?> LookupMaps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cancellation for graceful interruption (resume picks up from history).</summary>
    public CancellationToken CancellationToken { get; set; } = default;

    /// <summary>When set, items filtered out by delta dates are recorded here as Skipped.</summary>
    public CopyResult? DeltaSkipLog { get; set; }

    /// <summary>Max server-stamped Modified seen by the last LoadAllItemsAsync scan (delta baseline source).</summary>
    public DateTime? LastScanMaxModifiedUtc { get; private set; }

    // Partial-batch reconciliation state (set at the start of CopyAsync).
    // _lastAddedTargetId tracks the highest id we have RECORDED as added, so a failed batch only has to
    // ask for the ids above it (bounded) rather than every id above the run's baseline (unbounded, and
    // past ~5,000 rows a list-view-threshold error).
    private int _lastAddedTargetId;
    /// <summary>False when the target's max id could not be read: reconciliation must not guess.</summary>
    private bool _baselineKnown;
    private List? _batchTargetList;
    private int _addBaselineId;

    // Target multi-value taxonomy fields, loaded ONCE up front (a mid-batch ExecuteQuery would
    // prematurely flush queued AddItems). Multi values need SetFieldValueByValueCollection, which
    // requires the field object; single values assign directly and don't need this.
    private readonly Dictionary<string, TaxonomyField> _targetTaxFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source list CT string id -> target list CT string id (per-item content type assignment).</summary>
    public Dictionary<string, string> ContentTypeMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source item ids with unique permissions + the copier to apply them (CopyPermissions setting).</summary>
    public HashSet<int>? UniquePermissionItemIds { get; set; }

    /// <summary>Optional source internal name -> target internal name mapping (per-task field mapping).</summary>
    public Dictionary<string, string>? FieldNameMap { get; set; }
    public PermissionCopier? Permissions { get; set; }

    public ItemCopier(ClientContext sourceCtx, ClientContext targetCtx, UserResolver users)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
        _users = users;
    }

    /// <summary>
    /// Computes the writable, copyable field set shared by both lists.
    /// </summary>
    public async Task<List<(string InternalName, string TypeAsString)>> GetCopyFieldsAsync(List sourceList, List targetList)
    {
        _sourceCtx.Load(sourceList, l => l.RootFolder.ServerRelativeUrl, l => l.BaseType,
            l => l.Fields.Include(f => f.InternalName, f => f.TypeAsString, f => f.ReadOnlyField, f => f.Hidden));
        _targetCtx.Load(targetList, l => l.RootFolder.ServerRelativeUrl,
            l => l.Fields.Include(f => f.InternalName, f => f.TypeAsString, f => f.ReadOnlyField, f => f.Hidden));
        await _sourceCtx.ExecuteWithRetryAsync();
        await _targetCtx.ExecuteWithRetryAsync();

        var targetFieldTypes = targetList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.ReadOnlyField)
            .ToDictionary(f => f.InternalName, f => f.TypeAsString, StringComparer.OrdinalIgnoreCase);

        // Source data columns that would copy, except the target list has no such column, so
        // their values are silently dropped in a content-only copy (MergeSchema=false). The
        // caller surfaces this as a warning so metadata loss is not invisible.
        DroppedTargetMissingFields = sourceList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.ReadOnlyField
                && !NeverCopyFields.Contains(f.InternalName)
                && f.TypeAsString is not ("TaxonomyFieldType" or "TaxonomyFieldTypeMulti" or "Computed" or "Lookup" or "LookupMulti")
                && !targetFieldTypes.ContainsKey(FieldNameMap?.GetValueOrDefault(f.InternalName) ?? f.InternalName))
            .Select(f => f.InternalName)
            .ToList();

        return sourceList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.ReadOnlyField
                && !NeverCopyFields.Contains(f.InternalName)
                && targetFieldTypes.ContainsKey(FieldNameMap?.GetValueOrDefault(f.InternalName) ?? f.InternalName)
                // Taxonomy fields copy now (the schema copier binds the target column to the
                // same term set); their values are applied specially in ApplyFieldValuesAsync.
                && f.TypeAsString is not "Computed"
                // Lookups copy only when the schema copier wired them up.
                && (f.TypeAsString is not ("Lookup" or "LookupMulti") || LookupMaps.ContainsKey(f.InternalName)))
            .Select(f => (f.InternalName, f.TypeAsString))
            .ToList();
    }

    /// <summary>Source columns absent on the target from the last GetCopyFieldsAsync (metadata dropped in a content-only copy).</summary>
    public List<string> DroppedTargetMissingFields { get; private set; } = new();

    /// <summary>
    /// Records one Warning when a content-only copy (MergeSchema=false) will drop values for
    /// source columns the target list does not have. With MergeSchema=true the schema copier
    /// creates those columns first, so nothing is dropped and this stays silent.
    /// </summary>
    public void WarnDroppedFields(CopyOptions options, CopyResult result)
    {
        if (options.MergeSchema || DroppedTargetMissingFields.Count == 0) return;
        var names = string.Join(", ", DroppedTargetMissingFields.Take(12));
        if (DroppedTargetMissingFields.Count > 12) names += $", +{DroppedTargetMissingFields.Count - 12} more";
        result.Add("List", options.TargetListTitle, options.TargetListTitle, ItemCopyStatus.Warning,
            $"content-only copy: {DroppedTargetMissingFields.Count} source column(s) not on the target were skipped ({names}). " +
            "Add them to the target or copy with schema to keep their values.");
    }

    public async Task CopyAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        // Fields we will copy: writable on BOTH sides, not in the never list,
        // and not a skipped type. Title is writable and included.
        var copyFields = await GetCopyFieldsAsync(sourceList, targetList);
        WarnDroppedFields(options, result);
        await PrimeTargetTaxonomyFieldsAsync(targetList, copyFields);

        // See CopyOptions.PathBase: a folder-scoped copy is relative to that folder, not the list root.
        var sourceRoot = options.PathBase(sourceList.RootFolder.ServerRelativeUrl);
        var targetRoot = targetList.RootFolder.ServerRelativeUrl;

        // Partial-batch reconciliation baseline: items WE add get Ids above this. We are the
        // only writer, so on a batch failure we re-read items above it to learn which adds
        // actually committed (CSOM does not populate the client objects on a failed batch).
        _batchTargetList = targetList;
        var baseline = await MaxItemIdAsync(targetList);
        _baselineKnown = baseline.HasValue;
        _addBaselineId = baseline ?? 0;
        _lastAddedTargetId = _addBaselineId;

        await PruneUpsertMapAsync(targetList, options, result);

        // Page through all source items, folders included.
        var allItems = await LoadAllItemsAsync(sourceList, options);
        result.PlannedItems = allItems.Count;

        // Resolve every referenced user BEFORE the first write: resolution
        // executes queries on the target context and would flush half-built
        // items, silently dropping their field values.
        await _users.PreResolveAsync(CollectUserIds(allItems, copyFields));

        // Folders first, shallowest first, so parents exist before children.
        var ordered = allItems
            .OrderByDescending(i => i.FileSystemObjectType == FileSystemObjectType.Folder)
            .ThenBy(i => ((string)i["FileRef"]).Count(c => c == '/'))
            .ToList();

        var batch = new List<(ListItem TargetItem, string SourceRef, ListItem SourceItem, bool IsUpdate, string TargetPath)>();
        foreach (var sourceItem in ordered)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var fileRef = (string)sourceItem["FileRef"];
            var relativePath = fileRef.Length > sourceRoot.Length ? fileRef[(sourceRoot.Length + 1)..] : "";
            var targetPath = string.IsNullOrEmpty(relativePath) ? targetRoot : $"{targetRoot}/{relativePath}";

            if (options.ResumeSkipPaths?.Contains(fileRef) == true)
            {
                result.Add("Item", fileRef, targetPath, ItemCopyStatus.Skipped, "resume: already copied in interrupted run");
                continue;
            }

            try
            {
                if (sourceItem.FileSystemObjectType == FileSystemObjectType.Folder)
                {
                    await CopyFolderItemAsync(sourceItem, targetList, targetRoot, relativePath, copyFields, options, result);
                    continue;
                }

                // Upsert: items mapped from a previous run are UPDATED on the
                // target (keyed by persisted ids, NEVER by Title, so lists
                // with identical titles delta correctly). New items are added.
                ListItem targetItem;
                var isUpdate = options.UpsertItemMap != null
                    && options.UpsertItemMap.TryGetValue(sourceItem.Id, out var existingTargetId);
                if (isUpdate)
                {
                    targetItem = targetList.GetItemById(options.UpsertItemMap![sourceItem.Id]);
                    if (options.ExistingMode == Model.ExistingItemMode.Skip)
                    {
                        result.Add("Item", fileRef, targetPath, ItemCopyStatus.Skipped, "already exists (skip mode)");
                        continue;
                    }
                    if (options.ExistingMode == Model.ExistingItemMode.CopyIfNewer)
                    {
                        // Commit any queued batch writes FIRST. This read shares _targetCtx,
                        // and executing it while a batch is pending would flush those writes
                        // early, outside FlushBatchAsync, mis-attributing their Copied/Failed.
                        await FlushBatchAsync(batch, options, result);
                        _targetCtx.Load(targetItem, i => i["Modified"]);
                        await _targetCtx.ExecuteWithRetryAsync();
                        var sMod = sourceItem["Modified"] is DateTime sd ? sd : DateTime.MaxValue;
                        var tMod = targetItem["Modified"] is DateTime td ? td : DateTime.MinValue;
                        if (sMod <= tMod)
                        {
                            result.Add("Item", fileRef, targetPath, ItemCopyStatus.Skipped, "target item is already up to date");
                            continue;
                        }
                    }
                }
                else
                {
                    var creation = new ListItemCreationInformation();
                    var parentDir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(parentDir))
                        creation.FolderUrl = $"{targetRoot}/{parentDir}";
                    targetItem = targetList.AddItem(creation);
                }

                await ApplyFieldValuesAsync(sourceItem, targetItem, copyFields, result, fileRef);
                ApplyContentType(sourceItem, targetItem);
                if (options.PreserveAuthorsAndDates)
                    await ApplyAuthorsAndDatesAsync(sourceItem, targetItem);
                targetItem.UpdateOverwriteVersion();
                _targetCtx.Load(targetItem, i => i.Id);
                batch.Add((targetItem, fileRef, sourceItem, isUpdate, targetPath));

                if (batch.Count >= options.BatchSize)
                    await FlushBatchAsync(batch, options, result);
            }
            catch (Exception ex)
            {
                result.Add("Item", fileRef, targetPath, ItemCopyStatus.Failed, ex.Message);
            }
        }
        await FlushBatchAsync(batch, options, result);
    }

    private async Task FlushBatchAsync(List<(ListItem TargetItem, string SourceRef, ListItem SourceItem, bool IsUpdate, string TargetPath)> batch,
        CopyOptions options, CopyResult result)
    {
        if (batch.Count == 0) return;
        List<(ListItem Target, ListItem Source, string SourceRef)>? attachmentWork = null;
        List<(ListItem Target, ListItem Source, string SourceRef)>? permissionWork = null;
        try
        {
            await _targetCtx.ExecuteWithRetryAsync();
            foreach (var (targetItem, sourceRef, sourceItem, isUpdate, itemTargetPath) in batch)
            {
                result.Add("Item", sourceRef, itemTargetPath, ItemCopyStatus.Copied, isUpdate ? "updated (delta)" : null);
                result.ItemMappings.Add((sourceItem.Id, targetItem.Id));
                if (!isUpdate && targetItem.Id > _lastAddedTargetId) _lastAddedTargetId = targetItem.Id;
                if (options.CopyAttachments
                    && sourceItem.FieldValues.GetValueOrDefault("Attachments") is bool b && b)
                {
                    attachmentWork ??= new List<(ListItem, ListItem, string)>();
                    attachmentWork.Add((targetItem, sourceItem, sourceRef));
                }
                if (options.CopyPermissions && UniquePermissionItemIds?.Contains(sourceItem.Id) == true)
                {
                    permissionWork ??= new List<(ListItem, ListItem, string)>();
                    permissionWork.Add((targetItem, sourceItem, sourceRef));
                }
            }
        }
        catch (Exception ex)
        {
            await ReconcileFailedBatchAsync(batch, ex, result);
        }
        batch.Clear();

        if (attachmentWork != null)
            foreach (var (target, source, sourceRef) in attachmentWork)
                await CopyAttachmentsAsync(source, target, sourceRef, result);

        if (permissionWork != null && Permissions != null)
            foreach (var (target, source, sourceRef) in permissionWork)
            {
                try
                {
                    await Permissions.CopyItemPermissionsAsync(source, target, sourceRef, result);
                }
                catch (Exception ex)
                {
                    result.Add("Permission", sourceRef, "", ItemCopyStatus.Warning, $"permission copy failed: {ex.Message}");
                }
            }
    }

    private async Task<bool> FolderExistsAsync(string serverRelUrl)
    {
        try
        {
            var folder = _targetCtx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelUrl));
            _targetCtx.Load(folder, f => f.Exists);
            await _targetCtx.ExecuteWithRetryAsync();
            return folder.Exists;
        }
        catch { return false; }
    }

    /// <summary>
    /// The highest item id in the list, used as the "items above this are ours" baseline.
    ///
    /// Scope='RecursiveAll' is REQUIRED, not cosmetic: without it the query runs against the default
    /// (folder) scope, and on a list whose root folder holds more than 5,000 children SharePoint rejects
    /// it with "The attempted operation is prohibited because it exceeds the list view threshold" -- even
    /// with RowLimit 1 on the indexed ID. Verified live against a 15,000-item library: the default scope
    /// throws, RecursiveAll returns the max id. A silent 0 here is dangerous (it makes every pre-existing
    /// item look like one we added), so failures are logged rather than swallowed quietly.
    /// </summary>
    private async Task<int?> MaxItemIdAsync(List list)
    {
        try
        {
            var q = new CamlQuery
            {
                ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy>"
                          + "</Query><RowLimit>1</RowLimit></View>",
            };
            var top = list.GetItems(q);
            _targetCtx.Load(top, t => t.Include(i => i.Id));
            await _targetCtx.ExecuteWithRetryAsync();
            return top.AsEnumerable().Select(i => i.Id).DefaultIfEmpty(0).Max();
        }
        catch (Exception ex)
        {
            // NULL, not 0: a 0 baseline would make every pre-existing item look like one we just added,
            // and reconciliation would report other people's rows as copied. Unknown means "do not guess".
            Diagnostics.TraceLog.Write("Copy",
                $"could not read the target's max item id ({ex.Message}); batch reconciliation is disabled for this run");
            return null;
        }
    }

    /// <summary>
    /// A batch ExecuteQuery stops at the first server error, so items queued before it
    /// committed while CSOM left every client object un-populated. Since we are the only
    /// writer and new items get monotonically increasing Ids, re-read the items above the
    /// pre-copy baseline and correlate the committed ADDS to this batch by order (recording
    /// them Copied with their id map). Updates (upserts) are recorded Failed: retrying an
    /// update is idempotent, so no duplicate results. On any reconciliation error, fall back
    /// to the old "record all Failed" behavior.
    /// </summary>
    private async Task ReconcileFailedBatchAsync(
        List<(ListItem TargetItem, string SourceRef, ListItem SourceItem, bool IsUpdate, string TargetPath)> batch,
        Exception ex, CopyResult result)
    {
        var addEntries = batch.Where(b => !b.IsUpdate).ToList();
        try
        {
            List<int> ourAddIds = new();
            if (_batchTargetList != null && addEntries.Count > 0 && _baselineKnown)
            {
                // The newest N items by id, where N is this batch's add count -- we are the only writer, so
                // the ids we just created are the highest ones. Filter to "above the last id we recorded"
                // in memory rather than in CAML.
                //
                // The shape matters on a large list. There is deliberately NO <Where>: a filter that MATCHES
                // more than 5,000 rows throws "exceeds the list view threshold" even with a RowLimit, so
                // "ID > baseline" is a trap the moment the baseline is low or unknown (it matches the whole
                // list). Scope='RecursiveAll' + OrderBy on the indexed ID + a small RowLimit is the shape
                // verified safe against a 15,000-item library.
                var q = new CamlQuery
                {
                    ViewXml = "<View Scope='RecursiveAll'><Query><OrderBy><FieldRef Name='ID' Ascending='FALSE'/>"
                              + $"</OrderBy></Query><RowLimit>{addEntries.Count}</RowLimit></View>",
                };
                var created = _batchTargetList.GetItems(q);
                _targetCtx.Load(created, c => c.Include(i => i.Id));
                await _targetCtx.ExecuteWithRetryAsync();
                ourAddIds = created.AsEnumerable()
                    .Select(i => i.Id)
                    .Where(id => id > _lastAddedTargetId)   // ignore rows that were already there
                    .OrderBy(x => x)
                    .ToList();
            }
            // Ids above the last recorded add ARE this batch's commits, in queue order.
            var committedNow = ourAddIds.Count;

            for (var i = 0; i < addEntries.Count; i++)
            {
                var e = addEntries[i];
                if (i < committedNow)
                {
                    result.Add("Item", e.SourceRef, e.TargetPath, ItemCopyStatus.Copied, null);
                    result.ItemMappings.Add((e.SourceItem.Id, ourAddIds[i]));
                    if (ourAddIds[i] > _lastAddedTargetId) _lastAddedTargetId = ourAddIds[i];
                }
                else
                    result.Add("Item", e.SourceRef, e.TargetPath, ItemCopyStatus.Failed, ex.Message);
            }
            foreach (var e in batch.Where(b => b.IsUpdate))
                result.Add("Item", e.SourceRef, e.TargetPath, ItemCopyStatus.Failed, ex.Message);
        }
        catch
        {
            // Reconciliation itself failed: fall back to recording the whole batch Failed.
            foreach (var e in batch)
                result.Add("Item", e.SourceRef, e.TargetPath, ItemCopyStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Copies item attachments, then re-applies preserved dates (adding an
    /// attachment stamps Modified). Existing same-name attachments on the
    /// target (delta re-runs) are replaced.
    /// </summary>
    private async Task CopyAttachmentsAsync(ListItem sourceItem, ListItem targetItem, string sourceRef, CopyResult result)
    {
        try
        {
            var sourceFiles = sourceItem.AttachmentFiles;
            var targetFiles = targetItem.AttachmentFiles;
            _sourceCtx.Load(sourceFiles, fs => fs.Include(a => a.FileName, a => a.ServerRelativeUrl));
            _targetCtx.Load(targetFiles, fs => fs.Include(a => a.FileName));
            await _sourceCtx.ExecuteWithRetryAsync();
            await _targetCtx.ExecuteWithRetryAsync();
            var existing = new HashSet<string>(targetFiles.AsEnumerable().Select(a => a.FileName), StringComparer.OrdinalIgnoreCase);

            var copied = 0;
            foreach (var att in sourceFiles)
            {
                var file = _sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(att.ServerRelativeUrl));
                var stream = file.OpenBinaryStream();
                await _sourceCtx.ExecuteWithRetryAsync();
                using var ms = new MemoryStream();
                await stream.Value.CopyToAsync(ms);
                ms.Position = 0;

                if (existing.Contains(att.FileName))
                {
                    targetFiles.GetByFileName(att.FileName).DeleteObject();
                    await _targetCtx.ExecuteWithRetryAsync();
                }
                targetItem.AttachmentFiles.Add(new AttachmentCreationInformation
                {
                    FileName = att.FileName,
                    ContentStream = ms,
                });
                await _targetCtx.ExecuteWithRetryAsync();
                copied++;
            }

            if (copied > 0)
            {
                // Attachment writes stamped Modified; restore preserved values.
                await ApplyAuthorsAndDatesAsync(sourceItem, targetItem);
                targetItem.UpdateOverwriteVersion();
                await _targetCtx.ExecuteWithRetryAsync();
                result.Add("Attachment", sourceRef, "", ItemCopyStatus.Copied, $"{copied} attachment(s)");
            }
        }
        catch (Exception ex)
        {
            result.Add("Attachment", sourceRef, "", ItemCopyStatus.Warning, $"attachments failed: {ex.Message}");
        }
    }

    /// <summary>Every user id referenced by Author/Editor or user fields across all items.</summary>
    internal static IEnumerable<int> CollectUserIds(List<ListItem> items, List<(string InternalName, string TypeAsString)> copyFields)
    {
        var userFields = copyFields.Where(f => f.TypeAsString is "User" or "UserMulti")
            .Select(f => f.InternalName)
            .Concat(new[] { "Author", "Editor" });
        foreach (var item in items)
        {
            foreach (var field in userFields)
            {
                if (!item.FieldValues.TryGetValue(field, out var v) || v == null) continue;
                if (v is FieldUserValue u) yield return u.LookupId;
                else if (v is FieldUserValue[] us) foreach (var x in us) yield return x.LookupId;
            }
        }
    }

    /// <summary>Loads all items (RecursiveAll) with paging and optional filters.</summary>
    public async Task<List<ListItem>> LoadAllItemsAsync(List sourceList, CopyOptions options)
    {
        // Fast path: an explicit file/folder selection is fetched DIRECTLY by path instead of
        // paging the whole library. On a 150K-item source this turns a multi-minute scan for
        // "copy 3 files" into a few round trips.
        if (options.SelectedPaths.Count > 0 && options.ItemIds.Count == 0)
            return await LoadSelectedItemsAsync(sourceList, options);

        // Wildcard name patterns precompiled once per scan.
        var nameRegexes = options.NamePatterns.Count == 0 ? null : options.NamePatterns
            .Select(p => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        var folderScope = string.IsNullOrEmpty(options.SourceFolderServerRelativeUrl)
            ? null : options.SourceFolderServerRelativeUrl;
        try
        {
            return await ScanAsync(folderScope, null);
        }
        catch (Exception ex) when (folderScope != null && IsThresholdError(ex))
        {
            // The scoped folder is over the threshold, where a folder-scoped query is refused outright.
            // Scan the whole list (always pageable) and keep only what lives under that folder.
            Diagnostics.TraceLog.Write("Copy",
                $"source folder '{folderScope}' exceeds the list view threshold; scanning the whole list and filtering by path");
            LastScanMaxModifiedUtc = null;   // the abandoned attempt may have advanced it
            return await ScanAsync(null, folderScope.TrimEnd('/') + "/");
        }

        async Task<List<ListItem>> ScanAsync(string? scope, string? pathPrefix)
        {
        var items = new List<ListItem>();
        var query = new CamlQuery
        {
            ViewXml = $"<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>{options.PageSize}</RowLimit></View>",
        };
        if (scope != null) query.FolderServerRelativeUrl = scope;

        do
        {
            CancellationToken.ThrowIfCancellationRequested();   // cancellable during a long scan
            var page = sourceList.GetItems(query);
            // FieldValues loads by default; it cannot appear in an Include().
            _sourceCtx.Load(page);
            _sourceCtx.Load(page, p => p.Include(i => i.Id, i => i.FileSystemObjectType),
                p => p.ListItemCollectionPosition);
            await _sourceCtx.ExecuteWithRetryAsync();

            foreach (var item in page)
            {
                // Whole-list fallback: keep only what lives under the requested folder.
                if (pathPrefix != null
                    && !(item.FieldValues.GetValueOrDefault("FileRef") is string fr
                         && fr.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var itemModified = ReadUtc(item["Modified"]);

                if (nameRegexes != null)
                {
                    // Folders are skipped when filtering by name: parents of
                    // matched files are recreated on demand by the copier, so
                    // no empty shells appear for unselected folders.
                    if (item.FileSystemObjectType == FileSystemObjectType.Folder) continue;
                    var leaf = ((string)item["FileRef"]).Split('/')[^1];
                    if (!nameRegexes.Any(r => r.IsMatch(leaf)))
                    {
                        if (options.RecordSkippedItems && DeltaSkipLog != null)
                            DeltaSkipLog.Add("Item", (string)item["FileRef"], "", ItemCopyStatus.Skipped, "filter: name pattern not matched");
                        continue;
                    }
                }

                // Explicit ID selection (explorer multi-select on a list).
                // Unselected items are not logged as skips - thousands of
                // "not selected" rows would bury the real log.
                if (options.ItemIds.Count > 0 && !options.ItemIds.Contains(item.Id))
                    continue;

                // Surgical path selection (explorer multi-select of files and
                // folders): a listed file copies, a listed folder copies with
                // its whole subtree; everything else is silently left out and
                // parents of matches recreate on demand.
                if (options.SelectedPaths.Count > 0)
                {
                    var fileRef = (string)item["FileRef"];
                    var match = options.SelectedPaths.Any(p =>
                        fileRef.Equals(p, StringComparison.OrdinalIgnoreCase)
                        || fileRef.StartsWith(p.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
                    if (!match) continue;
                }

                // Date filter applies to FILES/items only. Folders must always pass so a
                // matched file can land inside them; otherwise a file whose parent folder is
                // older than the cutoff has nowhere to be created and fails.
                if ((options.ModifiedSinceUtc.HasValue || options.ModifiedBeforeUtc.HasValue)
                    && item.FileSystemObjectType != FileSystemObjectType.Folder)
                {
                    // Filter on the chosen date field (Modified by default, or Created).
                    var date = options.DateField == Model.DateFilterField.Created ? ReadUtc(item["Created"]) : itemModified;
                    var filtered =
                        (options.ModifiedSinceUtc.HasValue && date < options.ModifiedSinceUtc.Value) ||
                        (options.ModifiedBeforeUtc.HasValue && date >= options.ModifiedBeforeUtc.Value);
                    if (filtered)
                    {
                        // Delta/filter runs must SHOW what they skipped.
                        if (options.RecordSkippedItems && DeltaSkipLog != null)
                            DeltaSkipLog.Add("Item", (string)item["FileRef"], "", ItemCopyStatus.Skipped,
                                $"filtered out by date ({options.DateField.ToString().ToLowerInvariant()} {date:yyyy-MM-dd}Z)");
                        continue;
                    }
                }
                // Advance the delta baseline only over items we actually INCLUDE. Computing
                // it over every scanned item (before filters) let a filtered-out item's newer
                // date raise the baseline, so the next whole-scope delta skipped items that
                // were never copied.
                if (LastScanMaxModifiedUtc == null || itemModified > LastScanMaxModifiedUtc)
                    LastScanMaxModifiedUtc = itemModified;
                items.Add(item);
            }
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);

        return items;
        }
    }

    /// <summary>
    /// Loads exactly the picked files/folders by fetching each path directly. A picked file
    /// yields just that item; a picked folder yields the folder plus its subtree (a scan
    /// scoped to that folder, not the whole library). Parents of picked files are recreated
    /// on demand by the copier, so they are not included here.
    /// </summary>
    private async Task<List<ListItem>> LoadSelectedItemsAsync(List sourceList, CopyOptions options)
    {
        var items = new List<ListItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in options.SelectedPaths)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var path = raw.TrimEnd('/');

            // Resolve the item (the file API returns a folder's item too, so branch on the
            // ACTUAL type, not on which API resolved it).
            var item = await LoadItemByPathAsync(path, asFolder: false)
                    ?? await LoadItemByPathAsync(path, asFolder: true);
            if (item == null) continue;   // path no longer exists on the source

            AddUnique(items, seen, item);
            if (item.FileSystemObjectType == FileSystemObjectType.Folder)
                foreach (var child in await ScanFolderAsync(sourceList, path))
                    AddUnique(items, seen, child);
        }

        // A date filter still applies to the picked set (name patterns don't combine with an
        // explicit path selection). Folders always pass so their children can land.
        if (options.ModifiedSinceUtc.HasValue || options.ModifiedBeforeUtc.HasValue)
            items = items.Where(i =>
            {
                if (i.FileSystemObjectType == FileSystemObjectType.Folder) return true;
                var date = options.DateField == Model.DateFilterField.Created ? ReadUtc(i["Created"]) : ReadUtc(i["Modified"]);
                return !((options.ModifiedSinceUtc.HasValue && date < options.ModifiedSinceUtc.Value)
                       || (options.ModifiedBeforeUtc.HasValue && date >= options.ModifiedBeforeUtc.Value));
            }).ToList();

        foreach (var it in items)
        {
            var m = ReadUtc(it["Modified"]);
            if (LastScanMaxModifiedUtc == null || m > LastScanMaxModifiedUtc) LastScanMaxModifiedUtc = m;
        }
        return items;
    }

    private static void AddUnique(List<ListItem> items, HashSet<string> seen, ListItem item)
    {
        if (seen.Add((string)item["FileRef"])) items.Add(item);
    }

    private async Task<ListItem?> LoadItemByPathAsync(string serverRelPath, bool asFolder)
    {
        try
        {
            var item = asFolder
                ? _sourceCtx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelPath)).ListItemAllFields
                : _sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelPath)).ListItemAllFields;
            _sourceCtx.Load(item);
            _sourceCtx.Load(item, i => i.Id, i => i.FileSystemObjectType);
            await _sourceCtx.ExecuteWithRetryAsync();
            return item;
        }
        catch { return null; }   // wrong type (file vs folder) or gone: caller tries the other, or skips
    }

    private async Task<List<ListItem>> ScanFolderAsync(List sourceList, string folderServerRel)
    {
        try
        {
            return await PageItemsAsync(sourceList, folderServerRel, 200, null);
        }
        catch (Exception ex) when (IsThresholdError(ex))
        {
            // The folder is over the list view threshold. Scan the LIST instead and match on the path.
            Diagnostics.TraceLog.Write("Copy",
                $"folder '{folderServerRel}' exceeds the list view threshold; scanning the whole list and filtering by path");
            var prefix = folderServerRel.TrimEnd('/') + "/";
            return await PageItemsAsync(sourceList, null, 2000,
                i => i.FieldValues.GetValueOrDefault("FileRef") is string f
                     && f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Pages a list, optionally restricted to one folder subtree.
    ///
    /// <paramref name="folderServerRel"/> is the FAST path, but it is only usable below the threshold: a
    /// folder-scoped CAML query -- RecursiveAll or not, any page size -- is rejected with "exceeds the list
    /// view threshold" once that folder holds more than 5,000 items (verified live against a 6,000-file
    /// folder; it survives the first pages and throws deeper in, which is why a short probe misses it).
    /// Passing null scans the whole list, which pages safely at any size, and the caller filters by path.
    /// </summary>
    private async Task<List<ListItem>> PageItemsAsync(
        List sourceList, string? folderServerRel, int pageSize, Func<ListItem, bool>? keep)
    {
        var items = new List<ListItem>();
        var query = new CamlQuery
        {
            ViewXml = $"<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>{pageSize}</RowLimit></View>",
        };
        if (folderServerRel != null) query.FolderServerRelativeUrl = folderServerRel;
        do
        {
            CancellationToken.ThrowIfCancellationRequested();
            var page = sourceList.GetItems(query);
            _sourceCtx.Load(page);
            _sourceCtx.Load(page, p => p.Include(i => i.Id, i => i.FileSystemObjectType), p => p.ListItemCollectionPosition);
            await _sourceCtx.ExecuteWithRetryAsync();
            foreach (var item in page)
                if (keep == null || keep(item)) items.Add(item);
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return items;
    }

    /// <summary>SharePoint's 5,000-row list view threshold refusal.</summary>
    internal static bool IsThresholdError(Exception ex) =>
        ex.Message.Contains("list view threshold", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies plain field values, translating user fields through the resolver.</summary>
    public async Task ApplyFieldValuesAsync(ListItem sourceItem, ListItem targetItem,
        List<(string InternalName, string TypeAsString)> copyFields, CopyResult result, string sourceRef)
    {
        foreach (var (name, type) in copyFields)
        {
            if (!sourceItem.FieldValues.TryGetValue(name, out var value) || value == null) continue;
            var writeName = FieldNameMap?.GetValueOrDefault(name) ?? name;

            switch (type)
            {
                case "User":
                    if (value is FieldUserValue uv)
                    {
                        var id = await _users.ResolveAsync(uv.LookupId);
                        if (id.HasValue) targetItem[writeName] = new FieldUserValue { LookupId = id.Value };
                        else result.Add("Item", sourceRef, "", ItemCopyStatus.Warning, $"user field {name}: unresolved user dropped");
                    }
                    break;
                case "UserMulti":
                    if (value is FieldUserValue[] uvs)
                    {
                        var resolved = new List<FieldUserValue>();
                        foreach (var u in uvs)
                        {
                            var id = await _users.ResolveAsync(u.LookupId);
                            if (id.HasValue) resolved.Add(new FieldUserValue { LookupId = id.Value });
                        }
                        if (resolved.Count > 0) targetItem[writeName] = resolved.ToArray();
                    }
                    break;
                case "URL":
                    if (value is FieldUrlValue url)
                        targetItem[writeName] = new FieldUrlValue { Url = url.Url, Description = url.Description };
                    break;
                case "Lookup":
                    if (value is FieldLookupValue lv && LookupMaps.TryGetValue(name, out var map))
                    {
                        var mappedId = map == null ? lv.LookupId : map.GetValueOrDefault(lv.LookupId, -1);
                        if (mappedId > 0) targetItem[writeName] = new FieldLookupValue { LookupId = mappedId };
                        else result.Add("Item", sourceRef, "", ItemCopyStatus.Warning, $"lookup {name}: no matching target item for '{lv.LookupValue}'");
                    }
                    break;
                case "LookupMulti":
                    if (value is FieldLookupValue[] lvs && LookupMaps.TryGetValue(name, out var mmap))
                    {
                        var mapped = lvs
                            .Select(v => mmap == null ? v.LookupId : mmap.GetValueOrDefault(v.LookupId, -1))
                            .Where(id => id > 0)
                            .Select(id => new FieldLookupValue { LookupId = id })
                            .ToArray();
                        if (mapped.Length > 0) targetItem[writeName] = mapped;
                    }
                    break;
                case "TaxonomyFieldType":
                    if (value is TaxonomyFieldValue tfv && !string.IsNullOrEmpty(tfv.TermGuid))
                        targetItem[writeName] = new TaxonomyFieldValue
                        {
                            WssId = -1,   // let the target list resolve/create the hidden taxonomy entry
                            Label = tfv.Label,
                            TermGuid = MapTerm(tfv.TermGuid),
                        };
                    break;
                case "TaxonomyFieldTypeMulti":
                    // Multi values need SetFieldValueByValueCollection on the target field object
                    // (a plain string/array assignment is rejected). The field was pre-loaded.
                    var pairs = TaxonomyLabelGuidPairs(value);
                    if (pairs != null && _targetTaxFields.TryGetValue(writeName, out var tf))
                    {
                        var coll = new TaxonomyFieldValueCollection(_targetCtx, null, tf);
                        coll.PopulateFromLabelGuidPairs(pairs);
                        tf.SetFieldValueByValueCollection(targetItem, coll);
                    }
                    else if (pairs != null)
                    {
                        result.Add("Item", sourceRef, "", ItemCopyStatus.Warning,
                            $"managed metadata (multi) {name}: target column not available; value dropped");
                    }
                    break;
                default:
                    targetItem[writeName] = value;
                    break;
            }
        }
    }

    /// <summary>
    /// Drops upsert mappings whose TARGET item no longer exists, so those source items are re-ADDED
    /// instead of updated.
    ///
    /// A mapping points at a target item id from a previous run. If that item has since been deleted --
    /// by a user, or because the whole list was recreated -- GetItemById still builds fine (it is lazy) and
    /// the failure only surfaces when the batch executes: "Item does not exist. It may have been deleted by
    /// another user." And because writes are batched, ONE dead id fails every update queued with it, not
    /// just itself. Cheaper and safer to check the ids up front: one paged id-only read of the target.
    /// </summary>
    private async Task PruneUpsertMapAsync(List targetList, CopyOptions options, CopyResult result)
    {
        if (options.UpsertItemMap is not { Count: > 0 } map) return;

        var live = new HashSet<int>();
        var query = new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'><ViewFields><FieldRef Name='ID'/></ViewFields>" +
                      "<RowLimit Paged='TRUE'>5000</RowLimit></View>",
        };
        do
        {
            var page = targetList.GetItems(query);
            _targetCtx.Load(page, p => p.Include(i => i.Id), p => p.ListItemCollectionPosition);
            await _targetCtx.ExecuteWithRetryAsync();
            foreach (var item in page) live.Add(item.Id);
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);

        var stale = map.Where(kv => !live.Contains(kv.Value)).Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return;

        foreach (var sourceId in stale) map.Remove(sourceId);
        Diagnostics.TraceLog.Write("Copy",
            $"upsert map: {stale.Count} mapping(s) pointed at target items that no longer exist; those items will be re-added");
        result.Add("List", targetList.Title, targetList.Title, ItemCopyStatus.Info,
            $"{stale.Count} previously-copied item(s) no longer exist on the target; they will be re-added");
    }

    /// <summary>Remap a term GUID through the optional cross-tenant TermMap (identity by default).</summary>
    public Dictionary<Guid, Guid>? TermMap { get; set; }
    private string MapTerm(string termGuid)
    {
        if (TermMap != null && Guid.TryParse(termGuid, out var g) && TermMap.TryGetValue(g, out var mapped))
            return mapped.ToString();
        return termGuid;
    }

    /// <summary>
    /// Loads target multi-value taxonomy field objects once (SetFieldValueByValueCollection needs
    /// the field, and it can't be loaded mid-batch without flushing queued AddItems).
    /// </summary>
    public async Task PrimeTargetTaxonomyFieldsAsync(List targetList, List<(string InternalName, string TypeAsString)> copyFields)
    {
        _targetTaxFields.Clear();
        var multi = copyFields.Where(f => f.TypeAsString == "TaxonomyFieldTypeMulti").ToList();
        if (multi.Count == 0) return;
        foreach (var (name, _) in multi)
        {
            var write = FieldNameMap?.GetValueOrDefault(name) ?? name;
            var tf = _targetCtx.CastTo<TaxonomyField>(targetList.Fields.GetByInternalNameOrTitle(write));
            _targetCtx.Load(tf, f => f.InternalName);
            _targetTaxFields[write] = tf;
        }
        try { await _targetCtx.ExecuteWithRetryAsync(); }
        catch { _targetTaxFields.Clear(); }   // fields missing -> multi values warn, single still work
    }

    /// <summary>
    /// "Label|Guid;Label|Guid" (PopulateFromLabelGuidPairs format) from whatever CSOM handed back
    /// (a TaxonomyFieldValueCollection, a single value, or the raw note string), remapping GUIDs.
    /// </summary>
    private string? TaxonomyLabelGuidPairs(object value)
    {
        IEnumerable<(string Label, string Guid)>? terms = value switch
        {
            TaxonomyFieldValueCollection col => ((IEnumerable<TaxonomyFieldValue>)col).Select(v => (v.Label, v.TermGuid)).ToList(),
            TaxonomyFieldValue single => new[] { (single.Label, single.TermGuid) },
            _ => null,
        };
        if (terms == null)
        {
            // Fall back to parsing the note representation: "-1;#Label|Guid;#-1;#Label|Guid".
            var raw = value.ToString() ?? "";
            terms = raw.Split(";#", StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Contains('|'))
                .Select(p => { var i = p.IndexOf('|'); return (Label: p[..i], Guid: p[(i + 1)..]); })
                .ToList();
        }
        var mapped = terms.Where(t => !string.IsNullOrEmpty(t.Guid))
            .Select(t => $"{t.Label}|{MapTerm(t.Guid)}").ToList();
        return mapped.Count > 0 ? string.Join(";", mapped) : null;
    }

    /// <summary>Assigns the mapped content type (matched by name via SchemaCopier).</summary>
    private void ApplyContentType(ListItem sourceItem, ListItem targetItem)
    {
        if (ContentTypeMap.Count == 0) return;
        var sourceCtId = sourceItem.FieldValues.GetValueOrDefault("ContentTypeId")?.ToString();
        if (sourceCtId == null) return;
        // List item ids extend the list CT id; match on the longest prefix.
        var match = ContentTypeMap.Keys
            .Where(k => sourceCtId.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length).FirstOrDefault();
        if (match != null)
            targetItem["ContentTypeId"] = ContentTypeMap[match];
    }

    /// <summary>Sets Author/Editor (resolved) and Created/Modified on a not-yet-flushed item.</summary>
    private async Task ApplyAuthorsAndDatesAsync(ListItem sourceItem, ListItem targetItem)
    {
        var authorId = sourceItem.FieldValues.TryGetValue("Author", out var a) && a is FieldUserValue av
            ? await _users.ResolveAsync(av.LookupId) : null;
        var editorId = sourceItem.FieldValues.TryGetValue("Editor", out var e) && e is FieldUserValue ev
            ? await _users.ResolveAsync(ev.LookupId) : null;

        if (authorId.HasValue) targetItem["Author"] = new FieldUserValue { LookupId = authorId.Value };
        if (editorId.HasValue) targetItem["Editor"] = new FieldUserValue { LookupId = editorId.Value };
        targetItem["Created"] = ToWriteDate(sourceItem["Created"]);
        targetItem["Modified"] = ToWriteDate(sourceItem["Modified"]);
    }

    /// <summary>
    /// SP read-back DateTimes are UTC with Kind=Unspecified. CSOM serializes
    /// Unspecified/Local as local time, so convert explicitly to Local
    /// (Kind=Utc write is rejected on folder items on some tenants).
    /// </summary>
    internal static DateTime ToWriteDate(object value)
    {
        // Ground truth from the date-lab (raw REST observer, non-UTC machine):
        //  - CSOM DESERIALIZES DateTimes into the machine's LOCAL wall-clock
        //    digits with Kind=Unspecified.
        //  - On WRITE, Utc- and Local-kind values serialize EXACTLY;
        //    Unspecified is treated as machine-local.
        // A read-back is therefore already the local representation: stamping
        // Kind=Local restores the true instant, timezone-independently.
        var dt = (DateTime)value;
        return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : dt;
    }

    /// <summary>
    /// Folder items: ensure the folder exists, then apply metadata with the
    /// hybrid pattern (user fields via ValidateUpdateListItem first, dates
    /// via UpdateOverwriteVersion last).
    /// </summary>
    private async Task CopyFolderItemAsync(ListItem sourceItem, List targetList, string targetRoot,
        string relativePath, List<(string, string)> copyFields, CopyOptions options, CopyResult result)
    {
        var sourceRef = (string)sourceItem["FileRef"];
        var folderCreation = new ListItemCreationInformation
        {
            UnderlyingObjectType = FileSystemObjectType.Folder,
            LeafName = relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath,
            FolderUrl = relativePath.Contains('/')
                ? $"{targetRoot}/{relativePath[..relativePath.LastIndexOf('/')]}"
                : targetRoot,
        };

        ListItem folderItem;
        try
        {
            folderItem = targetList.AddItem(folderCreation);
            folderItem["Title"] = folderCreation.LeafName;
            folderItem.Update();
            _targetCtx.Load(folderItem, i => i.Id);
            await _targetCtx.ExecuteWithRetryAsync();
        }
        catch (ServerException)
        {
            // Language-agnostic "already exists": re-check whether the folder is actually
            // there rather than matching the server's message, which is localized (e.g.
            // "existe deja" on a French web) and would otherwise be recorded as a failure.
            if (await FolderExistsAsync($"{targetRoot}/{relativePath}"))
            {
                result.Add("Folder", sourceRef, $"{targetRoot}/{relativePath}", ItemCopyStatus.Skipped, "folder already exists");
                return;
            }
            throw;
        }

        if (options.PreserveAuthorsAndDates)
            await ApplyFolderMetadataAsync(sourceItem, folderItem, copyFields, result, sourceRef);

        result.Add("Folder", sourceRef, $"{targetRoot}/{relativePath}", ItemCopyStatus.Copied);
    }

    /// <summary>
    /// Folder metadata write with tenant-variability handling (lab-verified
    /// 2026-06-11 on gocleverpointcom):
    ///   Strategy A (preferred): users as FieldUserValue + dates, one
    ///   UpdateOverwriteVersion. Works on this tenant; some tenants reject
    ///   user-field writes on folders ("Invalid data ... read only").
    ///   Strategy B (fallback): one ValidateUpdateListItem with claims keys
    ///   and locale-formatted dates in the web's regional timezone.
    /// App/system principals (i:0i.t|...) cannot be people-picker-matched and
    /// are skipped; the target then shows the migrating app, which is the
    /// closest equivalent of a source system account.
    /// Public so FileCopier reuses it.
    /// </summary>
    public async Task ApplyFolderMetadataAsync(ListItem sourceItem, ListItem targetFolderItem,
        List<(string InternalName, string TypeAsString)> copyFields, CopyResult result, string sourceRef)
    {
        var author = await ResolveRealUserAsync(sourceItem, "Author");
        var editor = await ResolveRealUserAsync(sourceItem, "Editor");

        try
        {
            // Custom column values on the folder (e.g. Dept), not only authors/dates.
            await ApplyFieldValuesAsync(sourceItem, targetFolderItem, copyFields, result, sourceRef);
            if (author != null) targetFolderItem["Author"] = new FieldUserValue { LookupId = author.Value.Id };
            if (editor != null) targetFolderItem["Editor"] = new FieldUserValue { LookupId = editor.Value.Id };
            targetFolderItem["Created"] = ToWriteDate(sourceItem["Created"]);
            targetFolderItem["Modified"] = ToWriteDate(sourceItem["Modified"]);
            targetFolderItem.UpdateOverwriteVersion();
            await _targetCtx.ExecuteWithRetryAsync();
            return;
        }
        catch (ServerException ex) when (ex.Message.Contains("Invalid data", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("read only", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Folder", sourceRef, "", ItemCopyStatus.Warning,
                "tenant rejects folder user fields via UpdateOverwriteVersion; using form-update fallback");
        }

        // Strategy B fallback.
        var formValues = new List<ListItemFormUpdateValue>();
        if (author != null)
            formValues.Add(new ListItemFormUpdateValue { FieldName = "Author", FieldValue = $"[{{\"Key\":\"{author.Value.Login}\"}}]" });
        if (editor != null)
            formValues.Add(new ListItemFormUpdateValue { FieldName = "Editor", FieldValue = $"[{{\"Key\":\"{editor.Value.Login}\"}}]" });

        var (createdSite, modifiedSite) = await ToSiteLocalAsync(
            ReadUtc(sourceItem["Created"]), ReadUtc(sourceItem["Modified"]));
        var culture = await TargetCultureAsync();
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Created", FieldValue = Model.DateText.ForFormUpdate(createdSite, culture) });
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Modified", FieldValue = Model.DateText.ForFormUpdate(modifiedSite, culture) });

        var validation = targetFolderItem.ValidateUpdateListItem(formValues, false, "", false, false, "");
        await _targetCtx.ExecuteWithRetryAsync();
        foreach (var fv in validation.Where(v => v.HasException))
            result.Add("Folder", sourceRef, "", ItemCopyStatus.Warning, $"{fv.FieldName}: {fv.ErrorMessage}");
    }

    /// <summary>
    /// Document fallback for sites that silently IGNORE UpdateOverwriteVersion
    /// metadata (typical for user-context browser sign-ins): one
    /// ValidateUpdateListItem DOCUMENT update with claims keys and
    /// site-local date strings. Public so FileCopier can switch to it.
    /// </summary>
    public async Task ApplyDocumentMetadataFormUpdateAsync(ListItem sourceItem, ListItem targetItem,
        CopyResult result, string sourceRef)
    {
        var author = await ResolveRealUserAsync(sourceItem, "Author");
        var editor = await ResolveRealUserAsync(sourceItem, "Editor");
        var formValues = new List<ListItemFormUpdateValue>();
        if (author != null)
            formValues.Add(new ListItemFormUpdateValue { FieldName = "Author", FieldValue = $"[{{\"Key\":\"{author.Value.Login}\"}}]" });
        if (editor != null)
            formValues.Add(new ListItemFormUpdateValue { FieldName = "Editor", FieldValue = $"[{{\"Key\":\"{editor.Value.Login}\"}}]" });
        var (createdSite, modifiedSite) = await ToSiteLocalAsync(
            ReadUtc(sourceItem["Created"]), ReadUtc(sourceItem["Modified"]));
        var culture = await TargetCultureAsync();
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Created", FieldValue = Model.DateText.ForFormUpdate(createdSite, culture) });
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Modified", FieldValue = Model.DateText.ForFormUpdate(modifiedSite, culture) });

        // bNewDocumentUpdate=true: a document update, no version bump.
        var validation = targetItem.ValidateUpdateListItem(formValues, true, "", false, false, "");
        await _targetCtx.ExecuteWithRetryAsync();
        foreach (var fv in validation.Where(v => v.HasException))
            result.Add("File", sourceRef, "", ItemCopyStatus.Warning, $"{fv.FieldName}: {fv.ErrorMessage}");
    }

    /// <summary>
    /// Resolves a source Author/Editor to (target user id, target login),
    /// returning null for app/system principals or unresolvable users.
    /// </summary>
    private async Task<(int Id, string Login)?> ResolveRealUserAsync(ListItem sourceItem, string fieldName)
    {
        if (!sourceItem.FieldValues.TryGetValue(fieldName, out var v) || v is not FieldUserValue uv) return null;

        var source = _users.GetSourceUser(uv.LookupId);
        if (source != null && source.Value.Login.StartsWith("i:0i.t|", StringComparison.OrdinalIgnoreCase))
            return null;  // app principal: not matchable, skip

        var login = await _users.ResolveTargetLoginAsync(uv.LookupId);
        if (login == null || login.StartsWith("i:0i.t|", StringComparison.OrdinalIgnoreCase)) return null;
        var id = await _users.ResolveAsync(uv.LookupId);
        return id.HasValue ? (id.Value, login.Replace("\"", "")) : null;
    }

    internal static DateTime ReadUtc(object value)
    {
        // CSOM read-backs are LOCAL digits (see ToWriteDate ground truth).
        var dt = (DateTime)value;
        return dt.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime(),
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => dt,
        };
    }

    private Microsoft.SharePoint.Client.TimeZone? _targetTimeZone;
    private System.Globalization.CultureInfo? _targetCulture;

    /// <summary>Converts UTC values to the target web's regional timezone (for form-update date strings).</summary>
    private async Task<(DateTime Created, DateTime Modified)> ToSiteLocalAsync(DateTime createdUtc, DateTime modifiedUtc)
    {
        _targetTimeZone ??= _targetCtx.Web.RegionalSettings.TimeZone;
        var c = _targetTimeZone.UTCToLocalTime(createdUtc);
        var m = _targetTimeZone.UTCToLocalTime(modifiedUtc);
        await _targetCtx.ExecuteWithRetryAsync();
        return (c.Value, m.Value);
    }

    /// <summary>
    /// The target web's locale culture, cached. ValidateUpdateListItem parses date strings using the web's
    /// regional settings, so form-update dates MUST be formatted in this culture -- not a fixed US format,
    /// which a dd/MM web reads with day and month swapped.
    /// </summary>
    private async Task<System.Globalization.CultureInfo> TargetCultureAsync()
    {
        if (_targetCulture != null) return _targetCulture;
        _targetCtx.Load(_targetCtx.Web.RegionalSettings, r => r.LocaleId);
        await _targetCtx.ExecuteWithRetryAsync();
        _targetCulture = Model.DateText.CultureForLcid((int)_targetCtx.Web.RegionalSettings.LocaleId);
        return _targetCulture;
    }
}
