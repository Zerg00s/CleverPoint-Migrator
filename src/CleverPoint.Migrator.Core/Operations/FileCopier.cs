using System.Collections.Concurrent;
using System.Security.Cryptography;
using CleverPoint.Migrator.Core.Csom;
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

    // Connections, present only on the top-level copier, so it can spin up per-worker contexts
    // for parallel file transfer. Worker copiers are created without them (no nested workers).
    private readonly SpConnection? _source;
    private readonly SpConnection? _target;

    /// <summary>SHA-256 per source FileRef, captured while streaming. Used by the verifier.
    /// Parallel workers share the top copier's dictionary via <see cref="_hashLock"/>.</summary>
    public Dictionary<string, string> SourceHashes { get; private set; } = new();
    private object _hashLock = new();
    private void SetHash(string fileRef, string hash) { lock (_hashLock) SourceHashes[fileRef] = hash; }

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

    public Dictionary<string, string>? FieldNameMap { set => _itemCopier.FieldNameMap = value; }

    /// <summary>Max server-stamped Modified seen by the scan (delta baseline source).</summary>
    public DateTime? LastScanMaxModifiedUtc => _itemCopier.LastScanMaxModifiedUtc;

    public FileCopier(ClientContext sourceCtx, ClientContext targetCtx, UserResolver users,
        Http.SpRestClient? sourceRest = null, Http.SpRestClient? targetRest = null,
        SpConnection? source = null, SpConnection? target = null)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
        _users = users;
        _itemCopier = new ItemCopier(sourceCtx, targetCtx, users);
        _sourceRest = sourceRest;
        _targetRest = targetRest;
        _source = source;
        _target = target;
    }

    public async Task CopyAsync(List sourceList, List targetList, CopyOptions options, CopyResult result)
    {
        var copyFields = await _itemCopier.GetCopyFieldsAsync(sourceList, targetList);
        _itemCopier.WarnDroppedFields(options, result);
        await _itemCopier.PrimeTargetTaxonomyFieldsAsync(targetList, copyFields);
        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var targetWeb = _targetCtx.Web;
        var listRoot = targetList.RootFolder.ServerRelativeUrl;

        // Copy into a subfolder of the target list when one was chosen (drop onto a
        // folder); otherwise into the list root. Items keep their source-relative paths
        // beneath this root.
        var sub = options.TargetSubfolderRelative?.Trim('/');
        var targetRoot = string.IsNullOrEmpty(sub) ? listRoot : $"{listRoot}/{sub}";
        if (!string.IsNullOrEmpty(sub))
            await EnsureFolderAsync(targetWeb, listRoot, sub);   // create the destination folder chain first

        var allItems = await _itemCopier.LoadAllItemsAsync(sourceList, options);
        result.PlannedItems = allItems.Count;

        // Pre-resolve users before any target writes (see ItemCopier note).
        await _users.PreResolveAsync(ItemCopier.CollectUserIds(allItems, copyFields));

        var ordered = allItems
            .OrderByDescending(i => i.FileSystemObjectType == FileSystemObjectType.Folder)
            .ThenBy(i => ((string)i["FileRef"]).Count(c => c == '/'))
            .ToList();

        // OneNote notebooks are folders that directly hold a .onetoc2. Only the TOPMOST
        // such folder is marked on the target so SharePoint renders it as a notebook;
        // marking nested section groups would fracture a cohesive notebook.
        var notebookRoots = BuildNotebookRoots(allItems, sourceRoot);

        // Folders whose dates were set, so they can be re-stamped once all their contents are in
        // (adding files/subfolders re-stamps a folder's Modified to "now", clobbering its date).
        var restampFolders = new List<(ListItem Source, string FileRef, string TargetPath)>();

        // Folders first, sequentially: parent ordering, folder metadata, OneNote marking.
        foreach (var sourceItem in ordered.Where(i => i.FileSystemObjectType == FileSystemObjectType.Folder))
        {
            _itemCopier.CancellationToken.ThrowIfCancellationRequested();
            var fileRef = (string)sourceItem["FileRef"];
            var relativePath = fileRef[(sourceRoot.Length + 1)..];
            var targetPath = $"{targetRoot}/{relativePath}";
            try
            {
                await EnsureFolderAsync(targetWeb, targetRoot, relativePath);
                var folderItem = await GetItemByPathAsync(_targetCtx, targetList, targetPath, true);
                if (options.PreserveAuthorsAndDates && folderItem != null)
                {
                    await _itemCopier.ApplyFolderMetadataAsync(sourceItem, folderItem, copyFields, result, fileRef);
                    restampFolders.Add((sourceItem, fileRef, targetPath));
                }
                var isNotebook = folderItem != null && notebookRoots.Contains(relativePath);
                if (isNotebook)
                    await MarkOneNoteNotebookAsync(folderItem!, result, targetPath);
                result.Add(isNotebook ? "OneNote" : "Folder", fileRef, targetPath, ItemCopyStatus.Copied);
            }
            catch (Exception ex)
            {
                result.Add("Folder", fileRef, targetPath, ItemCopyStatus.Failed, ex.Message);
            }
        }

        // Files: apply the resume-skip + date filters up front, then copy the survivors either
        // sequentially or (opt-in) through a bounded pool of per-worker connections.
        var files = new List<ListItem>();
        foreach (var sourceItem in ordered.Where(i => i.FileSystemObjectType == FileSystemObjectType.File))
        {
            var fileRef = (string)sourceItem["FileRef"];
            var targetPath = $"{targetRoot}/{fileRef[(sourceRoot.Length + 1)..]}";
            if (options.ResumeSkipPaths?.Contains(fileRef) == true)
            {
                result.Add("File", fileRef, targetPath, ItemCopyStatus.Skipped, "resume: already copied in interrupted run");
                continue;
            }
            if (options.ModifiedSinceUtc.HasValue || options.ModifiedBeforeUtc.HasValue)
            {
                var date = options.DateField == Model.DateFilterField.Created
                    ? ItemCopier.ReadUtc(sourceItem["Created"]) : ItemCopier.ReadUtc(sourceItem["Modified"]);
                if ((options.ModifiedSinceUtc.HasValue && date < options.ModifiedSinceUtc.Value)
                    || (options.ModifiedBeforeUtc.HasValue && date >= options.ModifiedBeforeUtc.Value))
                {
                    if (options.RecordSkippedItems)
                        result.Add("File", fileRef, targetPath, ItemCopyStatus.Skipped,
                            $"filtered out by date ({options.DateField.ToString().ToLowerInvariant()} {date:yyyy-MM-dd}Z)");
                    continue;
                }
            }
            files.Add(sourceItem);
        }

        // Parallel only when asked, connections are available, and permissions aren't being
        // copied (that path is stateful). Otherwise the proven sequential path, unchanged.
        var degree = options.CopyPermissions ? 1 : Math.Max(1, options.ParallelFileTransfers);
        if (degree > 1 && _source != null && _target != null && files.Count > 1)
        {
            // A filtered selection's parent folders aren't in the scan (folder items are skipped),
            // so each file creates its chain on demand. Under parallelism that races several
            // workers to create the SAME folder ("... already exists"). Pre-create the distinct
            // chains sequentially here so the workers only ever find them already present.
            if (options.SelectedPaths.Count > 0 || options.NamePatterns.Count > 0)
            {
                var parents = files
                    .Select(i => ((string)i["FileRef"])[(sourceRoot.Length + 1)..])
                    .Where(rel => rel.Contains('/'))
                    .Select(rel => rel[..rel.LastIndexOf('/')])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(rel => rel.Length);
                foreach (var rel in parents)
                {
                    try { await EnsureFolderAsync(targetWeb, targetRoot, rel); }
                    catch (Exception ex) { result.Add("Folder", rel, $"{targetRoot}/{rel}", ItemCopyStatus.Failed, ex.Message); }
                }
            }
            await CopyFilesParallelAsync(files, sourceRoot, targetRoot, copyFields, options, result, degree);
        }
        else
        {
            foreach (var sourceItem in files)
            {
                _itemCopier.CancellationToken.ThrowIfCancellationRequested();
                var fileRef = (string)sourceItem["FileRef"];
                await CopyFileEntryAsync(sourceItem, fileRef, fileRef[(sourceRoot.Length + 1)..], targetRoot, copyFields, options, result);
            }
        }

        // Final pass: re-apply each folder's Created/Modified now that all its files and subfolders
        // are in place. Writing content into a folder re-stamps the folder's Modified to the copy
        // time, so the date set during the folder pass above was overwritten. A metadata-only folder
        // update does not touch its parent, so re-stamping deepest-first is safe and sufficient.
        if (options.PreserveAuthorsAndDates && restampFolders.Count > 0)
        {
            foreach (var f in restampFolders.OrderByDescending(x => x.TargetPath.Count(c => c == '/')))
            {
                _itemCopier.CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var folderItem = await GetItemByPathAsync(_targetCtx, targetList, f.TargetPath, true);
                    if (folderItem != null)
                        await _itemCopier.ApplyFolderMetadataAsync(f.Source, folderItem, copyFields, result, f.FileRef);
                }
                catch (Exception ex)
                {
                    result.Add("Folder", f.FileRef, f.TargetPath, ItemCopyStatus.Warning,
                        "could not restore folder dates after its contents were copied: " + ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Copies one FILE list item (already past the resume/date filters) on THIS copier's
    /// contexts: existing-mode check, large-file streaming or buffered upload, optional version
    /// trail, then field + author/date metadata. Shared by the sequential loop and each parallel
    /// worker (a worker runs it on its own connection, so no CSOM context is touched concurrently).
    /// </summary>
    private async Task CopyFileEntryAsync(ListItem sourceItem, string fileRef, string relativePath,
        string targetRoot, List<(string InternalName, string TypeAsString)> copyFields, CopyOptions options, CopyResult result)
    {
        var targetPath = $"{targetRoot}/{relativePath}";
        var targetWeb = _targetCtx.Web;
        var started = DateTime.UtcNow;
        try
        {
            // Existing-file handling (Skip / Copy-if-newer). Default Overwrite does no extra read.
            if (options.ExistingMode != Model.ExistingItemMode.Overwrite)
            {
                var targetMod = await ReadServerModifiedAsync(targetPath);
                if (targetMod.HasValue)
                {
                    if (options.ExistingMode == Model.ExistingItemMode.Skip)
                    {
                        result.Add("File", fileRef, targetPath, ItemCopyStatus.Skipped, "already exists (skip mode)");
                        return;
                    }
                    var sourceMod = sourceItem["Modified"] is DateTime sd ? sd : DateTime.MaxValue;
                    if (sourceMod <= targetMod.Value)
                    {
                        result.Add("File", fileRef, targetPath, ItemCopyStatus.Skipped, "target file is already up to date");
                        return;
                    }
                }
            }

            var parentDir = relativePath.Contains('/')
                ? $"{targetRoot}/{relativePath[..relativePath.LastIndexOf('/')]}"
                : targetRoot;

            // Filtered copies (selected paths / name patterns) skip folder items, so a matched
            // file's parent chain may not exist yet - recreate it on demand.
            if (relativePath.Contains('/')
                && (options.SelectedPaths.Count > 0 || options.NamePatterns.Count > 0))
                await EnsureFolderAsync(targetWeb, targetRoot, relativePath[..relativePath.LastIndexOf('/')]);

            // Large files stream through chunked upload sessions (O(slice) memory).
            var declaredSize = long.TryParse(sourceItem.FieldValues.GetValueOrDefault("File_x0020_Size")?.ToString(), out var ds) ? ds : 0;
            if (declaredSize >= options.LargeFileThresholdBytes && _sourceRest != null && _targetRest != null)
            {
                var bigRec = await CopyLargeFileAsync(sourceItem, fileRef, parentDir, relativePath, options, result);
                bigRec.Duration = DateTime.UtcNow - started;
                return;
            }

            var targetFolder = targetWeb.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(parentDir));

            // Older versions first (oldest -> newest) when requested, so the target accumulates
            // a real version trail before the current-content upload.
            var versionsUploaded = 0;
            if (options.MaxVersions > 1 && _sourceRest != null)
                versionsUploaded = await UploadOlderVersionsAsync(fileRef, targetFolder, relativePath, options, result);

            // Normal path: download (buffered, small files) and hash.
            var file = _sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(fileRef));
            var streamResult = file.OpenBinaryStream();
            await _sourceCtx.ExecuteQueryAsync();
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await streamResult.Value.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            SetHash(fileRef, Convert.ToHexString(SHA256.HashData(bytes)));
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
            await ApplyDocumentMetadataAsync(sourceItem, targetItem, options, result, fileRef, targetPath);

            var rec = result.Add("File", fileRef, targetPath, ItemCopyStatus.Copied,
                versionsUploaded > 0 ? $"with {versionsUploaded + 1} versions" : null);
            rec.SizeBytes = bytes.Length;
            rec.Duration = DateTime.UtcNow - started;
        }
        catch (Exception ex)
        {
            result.Add("File", fileRef, targetPath, ItemCopyStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Copies the given files through a bounded pool of workers, each with its own source+target
    /// connection (CSOM contexts are NOT thread-safe) and a user-resolver snapshot seeded from the
    /// primed parent so no worker performs concurrent EnsureUser. Records + SHA-256 hashes are
    /// written under locks; the REST client's per-host throttle is shared, so 429s still back off.
    /// </summary>
    private async Task CopyFilesParallelAsync(List<ListItem> files, string sourceRoot, string targetRoot,
        List<(string InternalName, string TypeAsString)> copyFields, CopyOptions options, CopyResult result, int degree)
    {
        var queue = new ConcurrentQueue<string>(files.Select(i => (string)i["FileRef"]));
        var workers = Enumerable.Range(0, Math.Min(degree, files.Count))
            .Select(_ => RunTransferWorkerAsync(queue, sourceRoot, targetRoot, copyFields, options, result));
        await Task.WhenAll(workers);
    }

    private async Task RunTransferWorkerAsync(ConcurrentQueue<string> queue, string sourceRoot, string targetRoot,
        List<(string InternalName, string TypeAsString)> copyFields, CopyOptions options, CopyResult result)
    {
        using var sctx = _source!.CreateContext();
        using var tctx = _target!.CreateContext();
        var worker = new FileCopier(sctx, tctx, _users.SnapshotFor(sctx, tctx), _sourceRest, _targetRest)
        {
            LookupMaps = _itemCopier.LookupMaps,
            CancellationToken = _itemCopier.CancellationToken,
            FieldNameMap = _itemCopier.FieldNameMap,
        };
        worker.SourceHashes = SourceHashes;   // share the parent's dictionary + its lock
        worker._hashLock = _hashLock;

        // Prime taxonomy columns on the worker's own target list (no-op when there are none).
        var wList = tctx.Web.Lists.GetByTitle(options.TargetListTitle);
        tctx.Load(wList, l => l.Title);
        await tctx.ExecuteQueryAsync();
        await worker._itemCopier.PrimeTargetTaxonomyFieldsAsync(wList, copyFields);

        while (queue.TryDequeue(out var fileRef))
        {
            _itemCopier.CancellationToken.ThrowIfCancellationRequested();
            var relativePath = fileRef[(sourceRoot.Length + 1)..];
            try
            {
                // Re-load the source item on the worker's own context (cross-context objects
                // from the main scan cannot be used here).
                var f = sctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(fileRef));
                var si = f.ListItemAllFields;
                sctx.Load(si);
                sctx.Load(si, i => i.FileSystemObjectType);
                await sctx.ExecuteQueryAsync();
                await worker.CopyFileEntryAsync(si, fileRef, relativePath, targetRoot, copyFields, options, result);
            }
            catch (Exception ex)
            {
                result.Add("File", fileRef, $"{targetRoot}/{relativePath}", ItemCopyStatus.Failed, ex.Message);
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
            $"{targetWebUrl}/_api/web/GetFolderByServerRelativePath(decodedUrl='{Esc(targetFolderServerRel)}')/Files/add(url='{Esc(name)}',overwrite=true)");

        var uploadId = Guid.NewGuid();
        var slice = new byte[options.UploadSliceBytes];
        long offset = 0;
        long total = 0;
        using var hash = SHA256.Create();

        try
        {
        await using (var download = await _sourceRest!.GetStreamAsync(
            $"{sourceWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{Esc(fileRef)}')/$value"))
        {
            var filled = 0;
            var first = true;
            var finished = false;   // true once a FinishUpload has committed the session
            while (true)
            {
                _itemCopier.CancellationToken.ThrowIfCancellationRequested();   // cancellable mid-upload
                var read = await download.ReadAsync(slice.AsMemory(filled, slice.Length - filled));
                if (read > 0) { filled += read; if (filled < slice.Length) continue; }

                var endOfStream = read == 0;
                var fileEndpoint = $"{targetWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{Esc(targetFileRel)}')";
                if (filled > 0)
                {
                    hash.TransformBlock(slice, 0, filled, null, 0);
                    if (first && endOfStream)
                    {
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset=0)", slice, filled);
                        finished = true;
                    }
                    else if (first)
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/StartUpload(uploadId=guid'{uploadId}')", slice, filled);
                    else if (endOfStream)
                    {
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, filled);
                        finished = true;
                    }
                    else
                        await _targetRest.PostBinaryAsync($"{fileEndpoint}/ContinueUpload(uploadId=guid'{uploadId}',fileOffset={offset})", slice, filled);
                    offset += filled;
                    total += filled;
                    first = false;
                    filled = 0;
                }
                if (endOfStream)
                {
                    // When the file size is an exact multiple of the slice size, the last
                    // full slice went out as ContinueUpload and end-of-stream arrives with an
                    // empty buffer, so the session was never committed. Commit it now with an
                    // empty FinishUpload at the final offset. (Skipped when a partial or
                    // single slice already finished, or when nothing was ever uploaded.)
                    if (!first && !finished)
                        await _targetRest.PostBinaryAsync(
                            $"{fileEndpoint}/FinishUpload(uploadId=guid'{uploadId}',fileOffset={offset})",
                            Array.Empty<byte>(), 0);
                    break;
                }
            }
        }
        }
        catch
        {
            // A partial or failed chunked upload leaves the 0-byte stub behind. Recycle it so
            // it does not masquerade as a real file on a later Skip / CopyIfNewer run (which
            // would treat the empty stub as "already there" and never re-copy the real bytes).
            try
            {
                await _targetRest.PostAsync(
                    $"{targetWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{Esc(targetFileRel)}')/recycle");
            }
            catch { /* best-effort cleanup */ }
            throw;
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        SetHash(fileRef, Convert.ToHexString(hash.Hash!));

        // Metadata on the uploaded file.
        var uploadedItem = await GetItemByPathAsync(_targetCtx, null!, targetFileRel, false);
        if (uploadedItem != null)
            await ApplyDocumentMetadataAsync(sourceItem, uploadedItem, options, result, fileRef, targetFileRel);
        else
            // The file uploaded but we could not read it back to stamp Author/Editor/dates.
            // Surface it instead of silently reporting a fully-successful copy.
            result.Add("File", fileRef, targetFileRel, ItemCopyStatus.Warning,
                "large file copied, but its metadata (authors/dates) could not be applied");

        var rec = result.Add("File", fileRef, targetFileRel, ItemCopyStatus.Copied, $"chunked upload, {total / 1048576.0:F0} MB");
        rec.SizeBytes = total;
        return rec;
    }

    private static string Esc(string s) => Uri.EscapeDataString(s.Replace("'", "''"));

    /// <summary>
    /// Uploads the last (MaxVersions - 1) OLDER versions of a file to the
    /// target, oldest first, so the final current-version upload lands on a
    /// real version trail. Version timestamps become migration time (SPO has
    /// no app-only way to back-date individual versions); contents are exact.
    /// </summary>
    private async Task<int> UploadOlderVersionsAsync(string fileRef, Folder targetFolder,
        string relativePath, CopyOptions options, Model.CopyResult result)
    {
        try
        {
            var sourceWebUrl = _sourceCtx.Url.TrimEnd('/');
            using var versionsDoc = await _sourceRest!.GetJsonAsync(
                $"{sourceWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{Esc(fileRef)}')/versions?$select=ID,VersionLabel,Size&$orderby=ID asc");
            var versions = versionsDoc.RootElement.GetProperty("value").EnumerateArray().ToList();
            if (versions.Count == 0) return 0;

            // Last N-1 older versions (the current content uploads after us).
            var wanted = versions.Skip(Math.Max(0, versions.Count - (options.MaxVersions - 1))).ToList();
            var name = relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;
            var uploaded = 0;
            foreach (var version in wanted)
            {
                var versionId = version.GetProperty("ID").GetInt32();
                var bytes = await _sourceRest.GetBytesAsync(
                    $"{sourceWebUrl}/_api/web/GetFileByServerRelativePath(decodedUrl='{Esc(fileRef)}')/versions({versionId})/$value");
                targetFolder.Files.Add(new FileCreationInformation
                {
                    Url = name,
                    ContentStream = new MemoryStream(bytes),
                    Overwrite = true,
                });
                await _targetCtx.ExecuteQueryAsync();
                uploaded++;
            }
            return uploaded;
        }
        catch (Exception ex)
        {
            result.Add("File", fileRef, "", Model.ItemCopyStatus.Warning, $"version history copy failed: {ex.Message}");
            return 0;
        }
    }

    private bool _metadataProbeDone;
    private bool _useFormUpdateMetadata;

    /// <summary>Test hook: start straight on the form-update strategy.</summary>
    public bool ForceFormUpdateMetadata { set { _useFormUpdateMetadata = value; _metadataProbeDone = value; } }

    /// <summary>
    /// The server's CURRENT Modified for a target file, via a FRESH CSOM
    /// object. Re-loading the object we just wrote returns our own pending
    /// values, which made the old probe compare us against ourselves.
    /// </summary>
    private async Task<DateTime?> ReadServerModifiedAsync(string targetPath)
    {
        var file = _targetCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(targetPath));
        var item = file.ListItemAllFields;
        _targetCtx.Load(item);
        await _targetCtx.ExecuteQueryAsync();
        return item.FieldValues.TryGetValue("Modified", out var m) && m is DateTime
            ? ItemCopier.ToWriteDate(m) : null;
    }

    private async Task ApplyDocumentMetadataAsync(ListItem sourceItem, ListItem targetItem,
        CopyOptions options, CopyResult result, string fileRef, string targetPath)
    {
        // Form-update strategy (set after the probe below detects a site that
        // ignores direct overwrites - typical for user-context sign-ins):
        // custom fields persist via one UpdateOverwriteVersion, then
        // authors/dates go through a ValidateUpdateListItem document update.
        if (options.PreserveAuthorsAndDates && _useFormUpdateMetadata)
        {
            targetItem.UpdateOverwriteVersion();
            await _targetCtx.ExecuteQueryAsync();
            await _itemCopier.ApplyDocumentMetadataFormUpdateAsync(sourceItem, targetItem, result, fileRef);
            return;
        }

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

        // Probe the FIRST file of the run: some sites silently IGNORE
        // system-field overwrites instead of failing them. When that happens,
        // heal this file via the form-update path and use it for the rest.
        if (options.PreserveAuthorsAndDates && !_metadataProbeDone)
        {
            _metadataProbeDone = true;
            var intended = ItemCopier.ToWriteDate(sourceItem["Modified"]);
            var actual = await ReadServerModifiedAsync(targetPath);
            // DateTime subtraction ignores Kind: compare true instants only.
            if (actual != null && Math.Abs((actual.Value.ToUniversalTime() - intended.ToUniversalTime()).TotalMinutes) > 2)
            {
                System.Diagnostics.Trace.WriteLine($"[CPMigrator] metadata probe mismatch: intended={intended:o} actual={actual:o} path={targetPath}");
                _useFormUpdateMetadata = true;
                await _itemCopier.ApplyDocumentMetadataFormUpdateAsync(sourceItem, targetItem, result, fileRef);
                result.Add("File", fileRef, "", Model.ItemCopyStatus.Warning,
                    "this site ignores direct metadata overwrites (typical for browser sign-in); "
                    + "switched to the form-update strategy for the rest of this run");

                // Verify the fallback actually took; if even that is refused,
                // say so in words instead of pretending it worked.
                var healed = await ReadServerModifiedAsync(targetPath);
                if (healed == null || Math.Abs((healed.Value.ToUniversalTime() - intended.ToUniversalTime()).TotalMinutes) > 2)
                    result.Add("File", fileRef, "", Model.ItemCopyStatus.Warning,
                        "this site refuses metadata preservation under the current sign-in. "
                        + "Connect with app + certificate to preserve authors and dates.");
            }
        }
    }

    // Relative folder paths that directly contain a .onetoc2 AND have no ancestor that
    // does (so a notebook's section groups don't each get marked). "" (the library
    // root) is never a markable folder, so it's excluded.
    private static HashSet<string> BuildNotebookRoots(IEnumerable<ListItem> items, string sourceRoot)
    {
        var withToc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (it.FileSystemObjectType != FileSystemObjectType.File) continue;
            var fileRef = (string)it["FileRef"];
            if (!fileRef.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileRef.Length <= sourceRoot.Length + 1) continue;
            var rel = fileRef[(sourceRoot.Length + 1)..];
            var slash = rel.LastIndexOf('/');
            if (slash < 0) continue;                       // .onetoc2 at library root: skip
            withToc.Add(rel[..slash]);
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in withToc)
            if (!withToc.Any(o => !string.Equals(o, f, StringComparison.OrdinalIgnoreCase)
                                  && f.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase)))
                roots.Add(f);
        return roots;
    }

    private async Task MarkOneNoteNotebookAsync(ListItem folderItem, CopyResult result, string targetPath)
    {
        // Setting HTML_x0020_File_x0020_Type cascades to the folder's ProgID and the
        // WOPI flag, so SharePoint opens it as a notebook instead of a plain folder.
        try
        {
            folderItem["HTML_x0020_File_x0020_Type"] = "OneNote.Notebook";
            folderItem.UpdateOverwriteVersion();
            await _targetCtx.ExecuteQueryAsync();
        }
        catch (Exception ex)
        {
            result.Add("Folder", targetPath, targetPath, Model.ItemCopyStatus.Warning,
                "copied, but could not mark as a OneNote notebook: " + ex.Message);
        }
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
                    var folder = targetWeb.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(next));
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
                    try
                    {
                        var parent = targetWeb.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(current));
                        // Path-safe create (AddUsingPath), so folder names containing % or # are
                        // taken literally instead of being decoded/mangled by the legacy Add().
                        parent.Folders.AddUsingPath(ResourcePath.FromDecodedUrl(next), new FolderCollectionAddParameters());
                        await _targetCtx.ExecuteQueryAsync();
                    }
                    catch (ServerException)
                    {
                        // A concurrent transfer worker may have created this folder between our
                        // existence probe and our create ("... already exists"). Only swallow the
                        // error if the folder is genuinely there now; otherwise it is real.
                        var check = targetWeb.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(next));
                        _targetCtx.Load(check, f => f.Exists);
                        await _targetCtx.ExecuteQueryAsync();
                        if (!check.Exists) throw;
                    }
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
                var folder = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelativePath));
                item = folder.ListItemAllFields;
            }
            else
            {
                var file = ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(serverRelativePath));
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
