using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

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
        await _sourceCtx.ExecuteQueryAsync();
        await _targetCtx.ExecuteQueryAsync();

        var targetFieldTypes = targetList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.ReadOnlyField)
            .ToDictionary(f => f.InternalName, f => f.TypeAsString, StringComparer.OrdinalIgnoreCase);
        return sourceList.Fields.AsEnumerable()
            .Where(f => !f.Hidden && !f.ReadOnlyField
                && !NeverCopyFields.Contains(f.InternalName)
                && targetFieldTypes.ContainsKey(FieldNameMap?.GetValueOrDefault(f.InternalName) ?? f.InternalName)
                && f.TypeAsString is not ("TaxonomyFieldType" or "TaxonomyFieldTypeMulti" or "Computed")
                // Lookups copy only when the schema copier wired them up.
                && (f.TypeAsString is not ("Lookup" or "LookupMulti") || LookupMaps.ContainsKey(f.InternalName)))
            .Select(f => (f.InternalName, f.TypeAsString))
            .ToList();
    }

    public async Task CopyAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        // Fields we will copy: writable on BOTH sides, not in the never list,
        // and not a skipped type. Title is writable and included.
        var copyFields = await GetCopyFieldsAsync(sourceList, targetList);

        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var targetRoot = targetList.RootFolder.ServerRelativeUrl;

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

        var batch = new List<(ListItem TargetItem, string SourceRef, ListItem SourceItem, bool IsUpdate)>();
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
                batch.Add((targetItem, fileRef, sourceItem, isUpdate));

                if (batch.Count >= options.BatchSize)
                    await FlushBatchAsync(batch, targetPath, options, result);
            }
            catch (Exception ex)
            {
                result.Add("Item", fileRef, targetPath, ItemCopyStatus.Failed, ex.Message);
            }
        }
        await FlushBatchAsync(batch, targetRoot, options, result);
    }

    private async Task FlushBatchAsync(List<(ListItem TargetItem, string SourceRef, ListItem SourceItem, bool IsUpdate)> batch,
        string targetPath, CopyOptions options, CopyResult result)
    {
        if (batch.Count == 0) return;
        List<(ListItem Target, ListItem Source, string SourceRef)>? attachmentWork = null;
        List<(ListItem Target, ListItem Source, string SourceRef)>? permissionWork = null;
        try
        {
            await _targetCtx.ExecuteQueryAsync();
            foreach (var (targetItem, sourceRef, sourceItem, isUpdate) in batch)
            {
                result.Add("Item", sourceRef, targetPath, ItemCopyStatus.Copied, isUpdate ? "updated (delta)" : null);
                result.ItemMappings.Add((sourceItem.Id, targetItem.Id));
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
            // The batch failed as a unit; record each item and continue.
            foreach (var (_, sourceRef, _, _) in batch)
                result.Add("Item", sourceRef, targetPath, ItemCopyStatus.Failed, ex.Message);
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
            await _sourceCtx.ExecuteQueryAsync();
            await _targetCtx.ExecuteQueryAsync();
            var existing = new HashSet<string>(targetFiles.AsEnumerable().Select(a => a.FileName), StringComparer.OrdinalIgnoreCase);

            var copied = 0;
            foreach (var att in sourceFiles)
            {
                var file = _sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(att.ServerRelativeUrl));
                var stream = file.OpenBinaryStream();
                await _sourceCtx.ExecuteQueryAsync();
                using var ms = new MemoryStream();
                await stream.Value.CopyToAsync(ms);
                ms.Position = 0;

                if (existing.Contains(att.FileName))
                {
                    targetFiles.GetByFileName(att.FileName).DeleteObject();
                    await _targetCtx.ExecuteQueryAsync();
                }
                targetItem.AttachmentFiles.Add(new AttachmentCreationInformation
                {
                    FileName = att.FileName,
                    ContentStream = ms,
                });
                await _targetCtx.ExecuteQueryAsync();
                copied++;
            }

            if (copied > 0)
            {
                // Attachment writes stamped Modified; restore preserved values.
                await ApplyAuthorsAndDatesAsync(sourceItem, targetItem);
                targetItem.UpdateOverwriteVersion();
                await _targetCtx.ExecuteQueryAsync();
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
        var items = new List<ListItem>();
        var query = new CamlQuery
        {
            ViewXml = $"<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>{options.PageSize}</RowLimit></View>",
        };
        if (!string.IsNullOrEmpty(options.SourceFolderServerRelativeUrl))
            query.FolderServerRelativeUrl = options.SourceFolderServerRelativeUrl;

        // Wildcard name patterns precompiled once per scan.
        var nameRegexes = options.NamePatterns.Count == 0 ? null : options.NamePatterns
            .Select(p => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        do
        {
            var page = sourceList.GetItems(query);
            // FieldValues loads by default; it cannot appear in an Include().
            _sourceCtx.Load(page);
            _sourceCtx.Load(page, p => p.Include(i => i.Id, i => i.FileSystemObjectType),
                p => p.ListItemCollectionPosition);
            await _sourceCtx.ExecuteQueryAsync();

            foreach (var item in page)
            {
                var itemModified = ReadUtc(item["Modified"]);
                if (LastScanMaxModifiedUtc == null || itemModified > LastScanMaxModifiedUtc)
                    LastScanMaxModifiedUtc = itemModified;

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

                if (options.ModifiedSinceUtc.HasValue || options.ModifiedBeforeUtc.HasValue)
                {
                    var modified = itemModified;
                    var filtered =
                        (options.ModifiedSinceUtc.HasValue && modified < options.ModifiedSinceUtc.Value) ||
                        (options.ModifiedBeforeUtc.HasValue && modified >= options.ModifiedBeforeUtc.Value);
                    if (filtered)
                    {
                        // Delta runs must SHOW what they skipped.
                        if (options.RecordSkippedItems && DeltaSkipLog != null)
                            DeltaSkipLog.Add("Item", (string)item["FileRef"], "", ItemCopyStatus.Skipped,
                                $"delta: unchanged (modified {modified:yyyy-MM-dd HH:mm}Z)");
                        continue;
                    }
                }
                items.Add(item);
            }
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);

        return items;
    }

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
                default:
                    targetItem[writeName] = value;
                    break;
            }
        }
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
            await _targetCtx.ExecuteQueryAsync();
        }
        catch (ServerException ex) when (ex.Message.Contains("already exists"))
        {
            result.Add("Folder", sourceRef, $"{targetRoot}/{relativePath}", ItemCopyStatus.Skipped, "folder already exists");
            return;
        }

        if (options.PreserveAuthorsAndDates)
            await ApplyFolderMetadataAsync(sourceItem, folderItem, result, sourceRef);

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
    public async Task ApplyFolderMetadataAsync(ListItem sourceItem, ListItem targetFolderItem, CopyResult result, string sourceRef)
    {
        var author = await ResolveRealUserAsync(sourceItem, "Author");
        var editor = await ResolveRealUserAsync(sourceItem, "Editor");

        try
        {
            if (author != null) targetFolderItem["Author"] = new FieldUserValue { LookupId = author.Value.Id };
            if (editor != null) targetFolderItem["Editor"] = new FieldUserValue { LookupId = editor.Value.Id };
            targetFolderItem["Created"] = ToWriteDate(sourceItem["Created"]);
            targetFolderItem["Modified"] = ToWriteDate(sourceItem["Modified"]);
            targetFolderItem.UpdateOverwriteVersion();
            await _targetCtx.ExecuteQueryAsync();
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
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Created", FieldValue = createdSite.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture) });
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Modified", FieldValue = modifiedSite.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture) });

        var validation = targetFolderItem.ValidateUpdateListItem(formValues, false, "", false, false, "");
        await _targetCtx.ExecuteQueryAsync();
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
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Created", FieldValue = createdSite.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture) });
        formValues.Add(new ListItemFormUpdateValue { FieldName = "Modified", FieldValue = modifiedSite.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.InvariantCulture) });

        // bNewDocumentUpdate=true: a document update, no version bump.
        var validation = targetItem.ValidateUpdateListItem(formValues, true, "", false, false, "");
        await _targetCtx.ExecuteQueryAsync();
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

    /// <summary>Converts UTC values to the target web's regional timezone (for form-update date strings).</summary>
    private async Task<(DateTime Created, DateTime Modified)> ToSiteLocalAsync(DateTime createdUtc, DateTime modifiedUtc)
    {
        _targetTimeZone ??= _targetCtx.Web.RegionalSettings.TimeZone;
        var c = _targetTimeZone.UTCToLocalTime(createdUtc);
        var m = _targetTimeZone.UTCToLocalTime(modifiedUtc);
        await _targetCtx.ExecuteQueryAsync();
        return (c.Value, m.Value);
    }
}
