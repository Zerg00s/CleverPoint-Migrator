using System.Security.Cryptography;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

/// <summary>
/// Copies a document library's folders and files with full metadata
/// preservation and per-file content validation (SHA-256 of the source
/// stream recorded during copy; the verifier re-downloads the target and
/// compares).
///
/// Document metadata: set Author/Editor/Created/Modified on the file's list
/// item, then UpdateOverwriteVersion() (SystemUpdate silently fails on
/// documents in some tenants). Folder metadata: hybrid pattern via ItemCopier.
/// </summary>
public class FileCopier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;
    private readonly UserResolver _users;
    private readonly ItemCopier _itemCopier;
    private readonly Http.SpRestClient? _sourceRest;
    private readonly Http.SpRestClient? _targetRest;

    /// <summary>SHA-256 per source FileRef, captured while streaming. Used by the verifier.</summary>
    public Dictionary<string, string> SourceHashes { get; } = new();

    /// <summary>Lookup translation maps, forwarded to the inner item copier.</summary>
    public Dictionary<string, Dictionary<int, int>?> LookupMaps
    {
        get => _itemCopier.LookupMaps;
        set => _itemCopier.LookupMaps = value;
    }

    /// <summary>Cancellation, forwarded to the inner item copier.</summary>
    public CancellationToken CancellationToken
    {
        get => _itemCopier.CancellationToken;
        set => _itemCopier.CancellationToken = value;
    }

    public void SetDeltaSkipLog(Model.CopyResult result) => _itemCopier.DeltaSkipLog = result;

    /// <summary>Max server-stamped Modified seen by the scan (delta baseline source).</summary>
    public DateTime? LastScanMaxModifiedUtc => _itemCopier.LastScanMaxModifiedUtc;

    public FileCopier(ClientContext sourceCtx, ClientContext targetCtx, UserResolver users,
        Http.SpRestClient? sourceRest = null, Http.SpRestClient? targetRest = null)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
        _users = users;
        _itemCopier = new ItemCopier(sourceCtx, targetCtx, users);
        _sourceRest = sourceRest;
        _targetRest = targetRest;
    }

    public async Task CopyAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        var copyFields = await _itemCopier.GetCopyFieldsAsync(sourceList, targetList);
        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var targetRoot = targetList.RootFolder.ServerRelativeUrl;
        var targetWeb = _targetCtx.Web;

        var allItems = await _itemCopier.LoadAllItemsAsync(sourceList, options);

        // Pre-resolve users before any target writes (see ItemCopier note).
        await _users.PreResolveAsync(ItemCopier.CollectUserIds(allItems, copyFields));

        var ordered = allItems
            .OrderByDescending(i => i.FileSystemObjectType == FileSystemObjectType.Folder)
            .ThenBy(i => ((string)i["FileRef"]).Count(c => c == '/'))
            .ToList();

        foreach (var sourceItem in ordered)
        {
            _itemCopier.CancellationToken.ThrowIfCancellationRequested();
            var fileRef = (string)sourceItem["FileRef"];
            var relativePath = fileRef[(sourceRoot.Length + 1)..];
            var targetPath = $"{targetRoot}/{relativePath}";
            var started = DateTime.UtcNow;

            if (options.ResumeSkipPaths?.Contains(fileRef) == true
                && sourceItem.FileSystemObjectType == FileSystemObjectType.File)
            {
                result.Add("File", fileRef, targetPath, ItemCopyStatus.Skipped, "resume: already copied in interrupted run");
                continue;
            }

            try
            {
                if (sourceItem.FileSystemObjectType == FileSystemObjectType.Folder)
                {
                    await EnsureFolderAsync(targetWeb, targetRoot, relativePath);
                    var folderItem = await GetItemByPathAsync(_targetCtx, targetList, targetPath, true);
                    if (options.PreserveAuthorsAndDates && folderItem != null)
                        await _itemCopier.ApplyFolderMetadataAsync(sourceItem, folderItem, result, fileRef);
                    result.Add("Folder", fileRef, targetPath, ItemCopyStatus.Copied);
                    continue;
                }

                var parentDir = relativePath.Contains('/')
                    ? $"{targetRoot}/{relativePath[..relativePath.LastIndexOf('/')]}"
                    : targetRoot;

                // Large files stream through chunked upload sessions
                // (O(slice) memory; works up to SPO's 250 GB limit).
                var declaredSize = long.TryParse(sourceItem.FieldValues.GetValueOrDefault("File_x0020_Size")?.ToString(), out var ds) ? ds : 0;
                if (declaredSize >= options.LargeFileThresholdBytes && _sourceRest != null && _targetRest != null)
                {
                    var bigRec = await CopyLargeFileAsync(sourceItem, fileRef, parentDir, relativePath, options, result);
                    bigRec.Duration = DateTime.UtcNow - started;
                    continue;
                }

                // Normal path: download (buffered, small files) and hash.
                var file = _sourceCtx.Web.GetFileByServerRelativeUrl(fileRef);
                var streamResult = file.OpenBinaryStream();
                await _sourceCtx.ExecuteQueryAsync();
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    await streamResult.Value.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }
                SourceHashes[fileRef] = Convert.ToHexString(SHA256.HashData(bytes));

                var targetFolder = targetWeb.GetFolderByServerRelativeUrl(parentDir);
                var uploaded = targetFolder.Files.Add(new FileCreationInformation
                {
                    Url = relativePath[(relativePath.LastIndexOf('/') + 1)..],
                    ContentStream = new MemoryStream(bytes),
                    Overwrite = true,
                });
                _targetCtx.Load(uploaded, f => f.ListItemAllFields.Id);
                await _targetCtx.ExecuteQueryAsync();

                // Metadata: custom fields + authors/dates, one UpdateOverwriteVersion.
                var targetItem = uploaded.ListItemAllFields;
                await _itemCopier.ApplyFieldValuesAsync(sourceItem, targetItem, copyFields, result, fileRef);
                await ApplyDocumentMetadataAsync(sourceItem, targetItem, options, result, fileRef);

                var rec = result.Add("File", fileRef, targetPath, ItemCopyStatus.Copied);
                rec.SizeBytes = bytes.Length;
                rec.Duration = DateTime.UtcNow - started;
            }
            catch (Exception ex)
            {
                result.Add(sourceItem.FileSystemObjectType == FileSystemObjectType.Folder ? "Folder" : "File",
                    fileRef, targetPath, ItemCopyStatus.Failed, ex.Message);
            }
        }
    }

    /// <summary>
    /// Streams one large file: REST $value download -> 10 MB slices -> REST
    /// upload session (StartUpload/ContinueUpload/FinishUpload). Incremental
    /// SHA-256 is recorded for verification. Public so the Migration API
    /// engine can route oversized files (its cap is 15 GB) through this path.
    /// </summary>
    public async Task<Model.ItemCopyRecord> CopyLargeFileAsync(ListItem sourceItem, string fileRef,
        string targetFolderServerRel, string relativePath, CopyOptions options, CopyResult result)
    {
        var sourceWebUrl = _sourceCtx.Url.TrimEnd('/');
        var targetWebUrl = _targetCtx.Url.TrimEnd('/');
        var name = relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;
        var targetFileRel = $"{targetFolderServerRel}/{name}";

        // 0-byte stub, then an upload session against it.
        await _targetRest!.PostAsync(
            $"{targetWebUrl}/_api/web/GetFolderByServerRelativeUrl('{Esc(targetFolderServerRel)}')/Files/add(url='{Esc(name)}',overwrite=true)");

        var uploadId = Guid.NewGuid();
        var slice = new byte[options.UploadSliceBytes];
        long offset = 0;
        long total = 0;
        using var hash = SHA256.Create();

        await using (var download = await _sourceRest!.GetStreamAsync(
            $"{sourceWebUrl}/_api/web/GetFileByServerRelativeUrl('{Esc(fileRef)}')/$value"))
        {
            var filled = 0;
            var first = true;
            while (true)
            {
                var read = await download.ReadAsync(slice.AsMemory(filled, slice.Length - filled));
                if (read > 0) { filled += read; if (filled < slice.Length) continue; }

                var endOfStream = read == 0;
                if (filled > 0)
                {
                    hash.TransformBlock(slice, 0, filled, null, 0);
                    var fileEndpoint = $"{targetWebUrl}/_api/web/GetFileByServerRelativeUrl('{Esc(targetFileRel)}')";
                    if (first && endOfStream)
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset=0)", slice, filled);
                    else if (first)
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/StartUpload(uploadId=guid'{uploadId}')", slice, filled);
                    else if (endOfStream)
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, filled);
                    else
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/ContinueUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, filled);
                    offset += filled;
                    total += filled;
                    first = false;
                    filled = 0;
                }
                if (endOfStream) break;
            }
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        SourceHashes[fileRef] = Convert.ToHexString(hash.Hash!);

        // Metadata on the uploaded file.
        var uploadedItem = await GetItemByPathAsync(_targetCtx, null!, targetFileRel, false);
        if (uploadedItem != null)
            await ApplyDocumentMetadataAsync(sourceItem, uploadedItem, options, result, fileRef);

        var rec = result.Add("File", fileRef, targetFileRel, ItemCopyStatus.Copied, $"chunked upload, {total / 1048576.0:F0} MB");
        rec.SizeBytes = total;
        return rec;
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private async Task ApplyDocumentMetadataAsync(ListItem sourceItem, ListItem targetItem,
        CopyOptions options, CopyResult result, string fileRef)
    {
        if (options.PreserveAuthorsAndDates)
        {
            var authorId = sourceItem.FieldValues.TryGetValue("Author", out var a) && a is FieldUserValue av
                ? await _users.ResolveAsync(av.LookupId) : null;
            var editorId = sourceItem.FieldValues.TryGetValue("Editor", out var e) && e is FieldUserValue ev
                ? await _users.ResolveAsync(ev.LookupId) : null;

            if (authorId.HasValue) targetItem["Author"] = new FieldUserValue { LookupId = authorId.Value };
            if (editorId.HasValue) targetItem["Editor"] = new FieldUserValue { LookupId = editorId.Value };
            targetItem["Created"] = ItemCopier.ToWriteDate(sourceItem["Created"]);
            targetItem["Modified"] = ItemCopier.ToWriteDate(sourceItem["Modified"]);
        }
        // One write persists custom fields AND (when enabled) authors/dates.
        targetItem.UpdateOverwriteVersion();
        await _targetCtx.ExecuteQueryAsync();
    }

    private readonly HashSet<string> _ensuredFolders = new(StringComparer.OrdinalIgnoreCase);

    private async Task EnsureFolderAsync(Web targetWeb, string targetRoot, string relativePath)
    {
        // Walk the path level by level. Loading a missing folder THROWS
        // (File Not Found) rather than returning Exists=false, so probe with
        // try/catch. Ensured paths are cached for the rest of the copy.
        var current = targetRoot;
        foreach (var segment in relativePath.Split('/'))
        {
            var next = $"{current}/{segment}";
            if (!_ensuredFolders.Contains(next))
            {
                var exists = true;
                try
                {
                    var folder = targetWeb.GetFolderByServerRelativeUrl(next);
                    _targetCtx.Load(folder, f => f.Exists);
                    await _targetCtx.ExecuteQueryAsync();
                    exists = folder.Exists;
                }
                catch (ServerException)
                {
                    exists = false;
                }

                if (!exists)
                {
                    var parent = targetWeb.GetFolderByServerRelativeUrl(current);
                    parent.Folders.Add(next);
                    await _targetCtx.ExecuteQueryAsync();
                }
                _ensuredFolders.Add(next);
            }
            current = next;
        }
    }

    /// <summary>Gets the list item behind a server-relative file/folder path on either context.</summary>
    public static async Task<ListItem?> GetItemByPathAsync(ClientContext ctx, List list, string serverRelativePath, bool isFolder)
    {
        try
        {
            ListItem item;
            if (isFolder)
            {
                var folder = ctx.Web.GetFolderByServerRelativeUrl(serverRelativePath);
                item = folder.ListItemAllFields;
            }
            else
            {
                var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativePath);
                item = file.ListItemAllFields;
            }
            ctx.Load(item);
            await ctx.ExecuteQueryAsync();
            return item;
        }
        catch
        {
            return null;
        }
    }
}
