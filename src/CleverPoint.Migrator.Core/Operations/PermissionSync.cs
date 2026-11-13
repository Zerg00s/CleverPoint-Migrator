using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Applies the source's permissions to content that already exists on the target. Nothing is created,
/// copied or deleted except role assignments.
///
/// Every list, folder, file and item with its own permissions on the source gets the same permissions
/// on its counterpart. Files and folders pair by path, using the same layout rules as a copy (source
/// browsing folder, target subfolder). List items pair through the item pairs a previous copy recorded,
/// because their IDs differ between lists. An object with no counterpart is reported and skipped.
///
/// Two callers: the permissions-only mode (the whole job), and a library copy with "Copy unique
/// permissions" ticked (a pass after the files are in place).
/// </summary>
public sealed class PermissionSync
{
    private readonly PermissionCopier _copier;
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;

    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Report source objects that have no counterpart on the target. True for a permissions-only run.
    /// False after a copy, where filters and skipped files make a missing counterpart expected.
    /// </summary>
    public bool ReportMissing { get; set; } = true;

    /// <summary>Also apply the list's own permissions (when it does not inherit from the site).</summary>
    public bool IncludeListLevel { get; set; } = true;

    /// <summary>
    /// True writes one File/Folder/Item line per object so the run's progress counts them. False writes
    /// "Permission" lines, which sit next to the copy's own lines without being counted twice.
    /// </summary>
    public bool CountAsContent { get; set; } = true;

    /// <summary>
    /// True makes the target match the source: assignments a target object already has are removed. That
    /// is the permissions-only mode. False only adds the source's assignments, which is what a copy does.
    /// </summary>
    public bool ReplaceExisting { get; set; } = true;

    public PermissionSync(PermissionCopier copier)
    {
        _copier = copier;
        _sourceCtx = copier.SourceContext;
        _targetCtx = copier.TargetContext;
    }

    private sealed record Entry(int Id, string FileRef, bool IsFolder, bool Unique);

    /// <summary>
    /// The list's own permissions, when the job covers all of the list and the source list does not
    /// inherit from its site. A job on a folder or a selection leaves the list itself alone.
    /// </summary>
    public async Task ApplyListLevelAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        var wholeList = options.SelectedPaths.Count == 0 && options.ItemIds.Count == 0
                        && string.IsNullOrEmpty(options.SourceFolderServerRelativeUrl)
                        && string.IsNullOrEmpty(options.TargetSubfolderRelative?.Trim('/'));
        if (!wholeList) return;

        _sourceCtx.Load(sourceList, l => l.HasUniqueRoleAssignments, l => l.RootFolder.ServerRelativeUrl);
        _targetCtx.Load(targetList, l => l.HasUniqueRoleAssignments, l => l.RootFolder.ServerRelativeUrl);
        await _sourceCtx.ExecuteWithRetryAsync();
        await _targetCtx.ExecuteWithRetryAsync();
        var sourceListRoot = sourceList.RootFolder.ServerRelativeUrl.TrimEnd('/');
        var targetListRoot = targetList.RootFolder.ServerRelativeUrl.TrimEnd('/');

        if (sourceList.HasUniqueRoleAssignments)
        {
            await ApplyAsync(sourceList, targetList, sourceListRoot, targetListRoot, "List", result);
        }
        else if (options.PermissionsOnly && options.ResetTargetWhereSourceInherits && targetList.HasUniqueRoleAssignments)
        {
            targetList.ResetRoleInheritance();
            await _targetCtx.ExecuteWithRetryAsync();
            result.Add("List", sourceListRoot, targetListRoot, ItemCopyStatus.Copied, "inheritance restored: the source list inherits from its site");
        }
    }

    public async Task RunAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        _sourceCtx.Load(sourceList, l => l.Title, l => l.BaseType, l => l.RootFolder.ServerRelativeUrl);
        _targetCtx.Load(targetList, l => l.Title, l => l.RootFolder.ServerRelativeUrl);
        await _sourceCtx.ExecuteWithRetryAsync();
        await _targetCtx.ExecuteWithRetryAsync();

        var isLibrary = sourceList.BaseType == BaseType.DocumentLibrary;
        var sourceListRoot = sourceList.RootFolder.ServerRelativeUrl.TrimEnd('/');
        var targetListRoot = targetList.RootFolder.ServerRelativeUrl.TrimEnd('/');
        var sourceRoot = options.PathBase(sourceListRoot).TrimEnd('/');
        var sub = options.TargetSubfolderRelative?.Trim('/');
        var targetRoot = string.IsNullOrEmpty(sub) ? targetListRoot : $"{targetListRoot}/{sub}";

        if (IncludeListLevel) await ApplyListLevelAsync(sourceList, targetList, options, result);

        // Source scan: which objects in scope have their own permissions.
        var scanned = await ScanAsync(sourceList, _sourceCtx);
        var inScope = scanned.Where(e => InScope(e, sourceRoot, options)).ToList();
        var unique = inScope.Where(e => e.Unique).ToList();
        if (CountAsContent) result.PlannedItems = unique.Count;
        result.Add("Progress", sourceListRoot, targetListRoot, ItemCopyStatus.Info,
            $"{unique.Count} of {inScope.Count} source object(s) in scope have their own permissions");

        var itemMap = options.UpsertItemMap;
        foreach (var entry in unique)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var type = entry.IsFolder ? "Folder" : isLibrary ? "File" : "Item";
            var outcome = CountAsContent ? type : "Permission";
            var byPath = isLibrary || entry.IsFolder;
            var targetPath = byPath ? targetRoot + entry.FileRef[sourceRoot.Length..] : "";
            try
            {
                ListItem? targetItem;
                if (byPath)
                {
                    targetItem = await FindByPathAsync(targetPath, entry.IsFolder);
                }
                else if (itemMap != null && itemMap.TryGetValue(entry.Id, out var targetId))
                {
                    targetItem = await FindByIdAsync(targetList, targetId);
                    targetPath = $"{targetListRoot}/{targetId}_.000";
                }
                else
                {
                    if (ReportMissing)
                        result.Add(outcome, entry.FileRef, "", ItemCopyStatus.Skipped,
                            "no matching target item is known: list items pair through the copy that created them, so copy this list with this tool first");
                    continue;
                }

                if (targetItem is null)
                {
                    if (ReportMissing)
                        result.Add(outcome, entry.FileRef, targetPath, ItemCopyStatus.Skipped, "not on the target; permissions not applied");
                    continue;
                }
                await ApplyAsync(sourceList.GetItemById(entry.Id), targetItem, entry.FileRef, targetPath, outcome, result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Add(outcome, entry.FileRef, targetPath, ItemCopyStatus.Failed, $"permissions not applied: {ex.Message}");
            }
        }

        if (options.PermissionsOnly && options.ResetTargetWhereSourceInherits)
            await ResetWhereSourceInheritsAsync(targetList, isLibrary, inScope, sourceRoot, targetRoot, itemMap, result);
    }

    private Task<bool> ApplyAsync(SecurableObject source, SecurableObject target, string sourceRef, string targetRef, string outcomeType, CopyResult result) =>
        _copier.CopyAsync(source, target, sourceRef, targetRef, result, ReplaceExisting, outcomeType);

    /// <summary>
    /// Target objects with their own permissions whose source counterpart exists and inherits get their
    /// inheritance back. A target object with no source counterpart is left alone: it is not part of
    /// what this run compares.
    /// </summary>
    private async Task ResetWhereSourceInheritsAsync(List targetList, bool isLibrary, List<Entry> sourceInScope,
        string sourceRoot, string targetRoot, Dictionary<int, int>? itemMap, CopyResult result)
    {
        var inheritingByPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inheritingTargetIds = new HashSet<int>();
        foreach (var e in sourceInScope.Where(e => !e.Unique))
        {
            if (isLibrary || e.IsFolder) inheritingByPath.Add(targetRoot + e.FileRef[sourceRoot.Length..]);
            else if (itemMap != null && itemMap.TryGetValue(e.Id, out var tid)) inheritingTargetIds.Add(tid);
        }

        foreach (var t in (await ScanAsync(targetList, _targetCtx)).Where(t => t.Unique))
        {
            CancellationToken.ThrowIfCancellationRequested();
            var byPath = isLibrary || t.IsFolder;
            if (byPath ? !inheritingByPath.Contains(t.FileRef) : !inheritingTargetIds.Contains(t.Id)) continue;
            try
            {
                targetList.GetItemById(t.Id).ResetRoleInheritance();
                await _targetCtx.ExecuteWithRetryAsync();
                result.Add("Permission", "", t.FileRef, ItemCopyStatus.Copied, "inheritance restored: the source inherits from its parent");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add("Permission", "", t.FileRef, ItemCopyStatus.Failed, $"inheritance not restored: {ex.Message}");
            }
        }
    }

    /// <summary>Every item, file and folder of a list with its path and whether it has its own permissions.</summary>
    private async Task<List<Entry>> ScanAsync(List list, ClientContext ctx)
    {
        var entries = new List<Entry>();
        await UniquePermissionScanner.ScanAsync(list, ctx, item => entries.Add(new Entry(item.Id, item.FileRef, item.IsFolder, item.Unique)), CancellationToken);
        return entries;
    }

    /// <summary>The same selection rules a copy uses: under the layout base, and inside the ticked paths or IDs when there are any.</summary>
    private static bool InScope(Entry e, string sourceRoot, CopyOptions options)
    {
        if (!e.FileRef.StartsWith(sourceRoot + "/", StringComparison.OrdinalIgnoreCase)) return false;
        if (options.SelectedPaths.Count == 0 && options.ItemIds.Count == 0) return true;
        if (options.ItemIds.Contains(e.Id)) return true;
        foreach (var selected in options.SelectedPaths)
        {
            var path = selected.TrimEnd('/');
            if (e.FileRef.Equals(path, StringComparison.OrdinalIgnoreCase)
                || e.FileRef.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async Task<ListItem?> FindByPathAsync(string serverRelativePath, bool isFolder)
    {
        var path = ResourcePath.FromDecodedUrl(serverRelativePath);
        var item = isFolder
            ? _targetCtx.Web.GetFolderByServerRelativePath(path).ListItemAllFields
            : _targetCtx.Web.GetFileByServerRelativePath(path).ListItemAllFields;
        _targetCtx.Load(item, i => i.Id);
        try
        {
            await _targetCtx.ExecuteWithRetryAsync();
            return item.ServerObjectIsNull == true ? null : item;
        }
        catch (ServerException ex) when (IsNotFound(ex))
        {
            return null;   // no file or folder at that path
        }
    }

    private async Task<ListItem?> FindByIdAsync(List targetList, int id)
    {
        var item = targetList.GetItemById(id);
        _targetCtx.Load(item, i => i.Id);
        try
        {
            await _targetCtx.ExecuteWithRetryAsync();
            return item;
        }
        catch (ServerException ex) when (IsNotFound(ex))
        {
            return null;   // deleted since the copy that paired it
        }
    }

    /// <summary>"It is not there", as opposed to access denied or any other failure, which must surface.</summary>
    private static bool IsNotFound(ServerException ex) =>
        ex.ServerErrorTypeName is "System.IO.FileNotFoundException" or "System.IO.DirectoryNotFoundException" or "System.ArgumentException"
        || ex.ServerErrorCode is -2147024894 or -2130575338 or -2147024809;
}
