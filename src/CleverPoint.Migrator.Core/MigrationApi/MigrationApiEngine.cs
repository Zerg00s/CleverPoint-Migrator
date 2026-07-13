using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.MigrationApi;

/// <summary>
/// The "Migration API (Azure blob)" engine, built for 10-100K item scale:
///  - splits the library into multiple migration jobs (ApiMaxItemsPerPackage,
///    per Microsoft's ~250 items/package guidance);
///  - streams ONE file at a time: download from source, AES-256-CBC encrypt
///    in memory, upload + snapshot, release. No local disk, flat RAM;
///  - defines each folder in exactly one job and references its GUID from
///    later jobs (identifiers are preserved by the API);
///  - submits the folder chunk first and waits, then pipelines file chunks:
///    chunk N+1 uploads while job N runs; one shared queue tracks them all.
/// </summary>
public class MigrationApiEngine
{
    private readonly SpConnection _source;
    private readonly SpConnection _target;
    private readonly AzureStorageRestClient _azure = new();

    public event Action<string>? OnProgress;

    public MigrationApiEngine(SpConnection source, SpConnection target)
    {
        _source = source;
        _target = target;
    }

    public async Task<CopyResult> CopyLibraryAsync(string sourceListTitle, CopyOptions options)
    {
        var result = new CopyResult();
        using var sourceCtx = _source.CreateContext();
        using var targetCtx = _target.CreateContext();

        // ---- Source + target structure ----
        var sourceList = sourceCtx.Web.Lists.GetByTitle(sourceListTitle);
        var users = new UserResolver(sourceCtx, targetCtx, null, options.UnresolvedUserFallback);
        await users.PrimeSourceUsersAsync();

        // Managed metadata before the schema: the target term store is a different object cross-tenant, so
        // the term sets must exist there BEFORE columns are bound to them. Without this the API engine's
        // taxonomy columns bind to the SOURCE SspId and are dead on arrival.
        sourceCtx.Load(sourceList, l => l.Fields.Include(f => f.InternalName, f => f.TypeAsString));
        await sourceCtx.ExecuteQueryAsync();
        var termStore = new Operations.TermStoreCopier(sourceCtx, targetCtx);
        await termStore.PrepareAsync(sourceList, result);
        var termMap = options.TermMap ?? termStore.ItemTermMap();
        string MapTerm(string guid) =>
            termMap != null && Guid.TryParse(guid, out var g) && termMap.TryGetValue(g, out var m)
                ? m.ToString() : guid;

        var schema = new SchemaCopier(sourceCtx, targetCtx) { TermStore = termStore };
        var targetList = await schema.CopyAsync(sourceList, options, result);

        // Structure-only copy: the list + schema are created above; skip all content.
        if (!options.CopyContent)
        {
            result.Add("List", sourceListTitle, options.TargetListTitle, ItemCopyStatus.Skipped,
                "schema-only copy: content skipped by settings");
            result.FinishedUtc = DateTime.UtcNow;
            return result;
        }

        targetCtx.Load(targetCtx.Web, w => w.Id, w => w.ServerRelativeUrl, w => w.Url);
        targetCtx.Load(targetList, l => l.Id, l => l.RootFolder.UniqueId, l => l.RootFolder.ServerRelativeUrl);
        await targetCtx.ExecuteWithRetryAsync();

        OnProgress?.Invoke("reading the source library… (this can take a while for very large libraries)");
        var itemCopier = new ItemCopier(sourceCtx, targetCtx, users);
        var copyFields = await itemCopier.GetCopyFieldsAsync(sourceList, targetList);
        var allItems = await itemCopier.LoadAllItemsAsync(sourceList, options);
        OnProgress?.Invoke($"loaded {allItems.Count:N0} item(s); resolving users and preparing packages…");
        await users.PreResolveAsync(ItemCopier.CollectUserIds(allItems, copyFields));

        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var listLeaf = targetList.RootFolder.ServerRelativeUrl[(targetCtx.Web.ServerRelativeUrl.TrimEnd('/').Length + 1)..];
        var textFieldNames = copyFields.Where(f => f.TypeAsString is "Text" or "Note" or "Choice")
            .Select(f => f.InternalName).ToList();

        // Managed metadata. The import bypasses the taxonomy event receivers, so the package must carry
        // the stored representation itself: the lookup into the site's TaxonomyHiddenList ("WssId;#Label|
        // Guid") plus the companion hidden note field. The mapper seeds a WssId for every term first --
        // a term never used in the target site has no row to point at.
        var taxonomy = new TaxonomyPackageMapper(targetCtx, targetList);
        await taxonomy.PrimeAsync(copyFields, allItems, MapTerm,
            options.FieldMap.Count > 0 ? options.FieldMap : null, msg => OnProgress?.Invoke(msg));

        if (taxonomy.UnresolvedTerms.Count > 0)
            result.Add("Field", string.Join(", ", taxonomy.UnresolvedTerms), "", ItemCopyStatus.Warning,
                $"{taxonomy.UnresolvedTerms.Count} term(s) could not be indexed in the target site; those values were not written");

        // ---- Shared chunk state ----
        var folderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var packageUsers = new List<PackageUser>();
        var userMapIds = new Dictionary<int, int>();
        var nextIntId = 1;

        async Task<int> MapUserAsync(object? fieldValue)
        {
            if (fieldValue is not FieldUserValue uv) return -1;
            var targetId = await users.ResolveAsync(uv.LookupId);
            if (!targetId.HasValue) return -1;
            if (userMapIds.TryGetValue(targetId.Value, out var mapped)) return mapped;
            var login = await users.ResolveTargetLoginAsync(uv.LookupId);
            if (login == null || login.StartsWith("i:0i.t|", StringComparison.OrdinalIgnoreCase)) return -1;
            var src = users.GetSourceUser(uv.LookupId);
            var mapId = packageUsers.Count + 1;
            packageUsers.Add(new PackageUser { MapId = mapId, Login = login, Name = src?.Title ?? login, Email = src?.Email ?? "" });
            userMapIds[targetId.Value] = mapId;
            return mapId;
        }

        // ---- Build folder metadata + assign folder ids upfront ----
        var folderItems = allItems.Where(i => i.FileSystemObjectType == FileSystemObjectType.Folder)
            .OrderBy(i => ((string)i["FileRef"]).Count(c => c == '/')).ToList();

        // Hybrid routing: the Migration API caps files at 15 GB and our
        // package path holds one file in RAM, so oversized files go through
        // the classic streaming chunked-upload path after the jobs finish.
        // Returns -1 when the size is unknown/unparseable. Such files take the SAFE
        // streaming path: a missing or mis-declared File_x0020_Size must never route a
        // giant file through the in-RAM package path (OOM / >2 GB array / 5 GiB PutBlob).
        long DeclaredSize(ListItem i) =>
            long.TryParse(i.FieldValues.GetValueOrDefault("File_x0020_Size")?.ToString(), out var s) ? s : -1;
        var fileItems = allItems.Where(i => i.FileSystemObjectType == FileSystemObjectType.File
            && DeclaredSize(i) >= 0 && DeclaredSize(i) < options.LargeFileThresholdBytes).ToList();
        var largeItems = allItems.Where(i => i.FileSystemObjectType == FileSystemObjectType.File
            && (DeclaredSize(i) < 0 || DeclaredSize(i) >= options.LargeFileThresholdBytes)).ToList();

        var folderMeta = new List<PackageFolder>();
        foreach (var item in folderItems)
        {
            var rel = ((string)item["FileRef"])[(sourceRoot.Length + 1)..];
            var folder = new PackageFolder
            {
                LibraryRelativePath = rel,
                CreatedUtc = Operations.ItemCopier.ReadUtc(item["Created"]),
                ModifiedUtc = Operations.ItemCopier.ReadUtc(item["Modified"]),
                AuthorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Author")),
                EditorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Editor")),
            };
            // Folders carry columns too, taxonomy included.
            foreach (var fieldName in textFieldNames)
                if (item.FieldValues.TryGetValue(fieldName, out var v) && v is string s && s.Length > 0)
                    folder.Fields.Add(new PackageFieldValue { Name = fieldName, Value = s, Type = "Text" });
            folder.Fields.AddRange(taxonomy.Emit(item, MapTerm));
            folderMeta.Add(folder);
        }
        var allFolderPaths = folderMeta.Select(f => f.LibraryRelativePath)
            .Concat(fileItems.Select(i => ParentDir(((string)i["FileRef"])[(sourceRoot.Length + 1)..])).Where(d => d.Length > 0))
            // Include the parents of the large (hybrid) files too, or a filtered/delta run
            // whose only content under a folder is a big file leaves that folder undefined,
            // and the hybrid upload then has no parent to land in.
            .Concat(largeItems.Select(i => ParentDir(((string)i["FileRef"])[(sourceRoot.Length + 1)..])).Where(d => d.Length > 0))
            .SelectMany(ExpandChain).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Count(c => c == '/')).ToList();
        foreach (var path in allFolderPaths)
            folderIds[path] = Guid.NewGuid();

        // ---- Provision the shared queue once ----
        var queue = targetCtx.Site.ProvisionMigrationQueue();
        await targetCtx.ExecuteWithRetryAsync();
        var queueUri = queue.Value.JobQueueUri;

        MigrationPackageBuilder NewBuilder() => new()
        {
            TargetWebId = targetCtx.Web.Id,
            TargetWebUrl = targetCtx.Web.ServerRelativeUrl,
            TargetSiteUrl = targetCtx.Web.Url,
            TargetListId = targetList.Id,
            ListUrlLeaf = listLeaf,
            TargetRootFolderId = targetList.RootFolder.UniqueId,
            FolderIds = folderIds,
        };

        var pendingJobs = new Dictionary<Guid, byte[]>();  // job id -> encryption key
        var totalBytes = 0L;
        var targetRootSrv = targetList.RootFolder.ServerRelativeUrl;

        // Per-item logging: the API submits batch jobs and emits no per-file success
        // event, so we remember each job's folders/files and log them as Copied once
        // the jobs finish (minus any file the job reported as failed).
        var jobEmits = new Dictionary<Guid, List<(string Type, string Source, string Target)>>();
        var failedRelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // rel path -> reason
        var failedJobs = new HashSet<Guid>();  // jobs that ended in a fatal error or timed out

        async Task<Guid> SubmitChunkAsync(MigrationPackageBuilder builder, List<ListItem> chunkFiles, int chunkNo)
        {
            // Fresh containers per job (Manifest.xml must sit at container root).
            var containers = targetCtx.Site.ProvisionMigrationContainers();
            await targetCtx.ExecuteWithRetryAsync();
            var key = containers.Value.EncryptionKey;

            // Stream the chunk's files one at a time.
            if (chunkFiles.Count > 0)
                OnProgress?.Invoke($"chunk {chunkNo}: packaging {chunkFiles.Count:N0} file(s)…");
            var packagedInChunk = 0;
            foreach (var item in chunkFiles)
            {
                var fileRef = (string)item["FileRef"];
                var rel = fileRef[(sourceRoot.Length + 1)..];
                var blobName = $"{Guid.NewGuid()}.dat";
                if (++packagedInChunk % 100 == 0)
                    OnProgress?.Invoke($"chunk {chunkNo}: packaged {packagedInChunk:N0}/{chunkFiles.Count:N0} file(s)…");

                var file = sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(fileRef));
                var stream = file.OpenBinaryStream();
                await sourceCtx.ExecuteWithRetryAsync();
                byte[] cipher;
                string iv;
                long size;
                using (var ms = new MemoryStream())
                {
                    await stream.Value.CopyToAsync(ms);
                    size = ms.Length;
                    (cipher, iv) = Aes256Cbc.Encrypt(ms.ToArray(), key);
                }
                await _azure.UploadBlobWithSnapshotAsync(containers.Value.DataContainerUri, blobName, cipher, iv);
                totalBytes += size;

                var packageFile = new PackageFile
                {
                    LibraryRelativePath = rel,
                    BlobName = blobName,
                    FileSize = size,
                    CreatedUtc = Operations.ItemCopier.ReadUtc(item["Created"]),
                    ModifiedUtc = Operations.ItemCopier.ReadUtc(item["Modified"]),
                    AuthorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Author")),
                    EditorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Editor")),
                };
                foreach (var fieldName in textFieldNames)
                    if (item.FieldValues.TryGetValue(fieldName, out var v) && v is string s && s.Length > 0)
                        packageFile.Fields.Add(new PackageFieldValue { Name = fieldName, Value = s, Type = "Text" });
                packageFile.Fields.AddRange(taxonomy.Emit(item, MapTerm));
                builder.Files.Add(packageFile);
            }

            builder.Users.AddRange(packageUsers);   // full user map in every package
            builder.IntIdStart = nextIntId;
            nextIntId += builder.FoldersToDefine.Count + builder.Files.Count;

            foreach (var (name, bytes) in builder.Build())
            {
                var (cipher, iv) = Aes256Cbc.Encrypt(bytes, key);
                await _azure.UploadBlobWithSnapshotAsync(containers.Value.MetadataContainerUri, name, cipher, iv);
            }

            var jobId = targetCtx.Site.CreateMigrationJobEncrypted(
                targetCtx.Web.Id, containers.Value.DataContainerUri, containers.Value.MetadataContainerUri,
                queueUri, new EncryptionOption { AES256CBCKey = key });
            await targetCtx.ExecuteWithRetryAsync();
            pendingJobs[jobId.Value] = key;

            // Remember this job's items so they can be logged as copied once it ends.
            var emits = new List<(string, string, string)>();
            foreach (var f in builder.Folders)
                emits.Add(("Folder", $"{sourceRoot}/{f.LibraryRelativePath}", $"{targetRootSrv}/{f.LibraryRelativePath}"));
            foreach (var item in chunkFiles)
            {
                var fr = (string)item["FileRef"];
                emits.Add(("File", fr, $"{targetRootSrv}/{fr[(sourceRoot.Length + 1)..]}"));
            }
            jobEmits[jobId.Value] = emits;

            OnProgress?.Invoke($"chunk {chunkNo}: job {jobId.Value} submitted ({builder.FoldersToDefine.Count} folders, {builder.Files.Count} files)");
            return jobId.Value;
        }

        // ---- Chunk 1: ALL folders (+ leading files), then WAIT so later
        //      chunks can rely on the folder structure existing. ----
        var maxItems = Math.Max(10, options.ApiMaxItemsPerPackage);
        var fileQueue = new Queue<ListItem>(fileItems);
        var chunkNo = 1;

        var firstBuilder = NewBuilder();
        firstBuilder.FoldersToDefine = new HashSet<string>(allFolderPaths, StringComparer.OrdinalIgnoreCase);
        firstBuilder.Folders.AddRange(folderMeta);
        var firstChunkFiles = new List<ListItem>();
        while (fileQueue.Count > 0 && firstBuilder.FoldersToDefine.Count + firstChunkFiles.Count < maxItems)
            firstChunkFiles.Add(fileQueue.Dequeue());
        var firstJob = await SubmitChunkAsync(firstBuilder, firstChunkFiles, chunkNo++);
        await WaitForJobsAsync(targetCtx, queueUri, new[] { firstJob }, pendingJobs, result, failedRelPaths, failedJobs);

        // The first chunk defines every target folder. If it failed fatally, those folders
        // do not exist, so every later chunk would import into missing parents. Stop now
        // rather than pay to upload the whole library into a cascade of failures.
        if (failedJobs.Contains(firstJob))
        {
            OnProgress?.Invoke("first chunk (folder definitions) failed; aborting the remaining chunks");
            result.Add("List", sourceListTitle, options.TargetListTitle, ItemCopyStatus.Failed,
                "the folder-defining first chunk failed; remaining content was not attempted");
            result.FinishedUtc = DateTime.UtcNow;
            return result;
        }

        // ---- Remaining chunks: upload + submit pipelined, wait at the end ----
        var laterJobs = new List<Guid>();
        while (fileQueue.Count > 0)
        {
            var builder = NewBuilder();
            var chunkFiles = new List<ListItem>();
            while (fileQueue.Count > 0 && chunkFiles.Count < maxItems)
                chunkFiles.Add(fileQueue.Dequeue());
            laterJobs.Add(await SubmitChunkAsync(builder, chunkFiles, chunkNo++));
        }
        if (laterJobs.Count > 0)
            await WaitForJobsAsync(targetCtx, queueUri, laterJobs, pendingJobs, result, failedRelPaths, failedJobs);

        OnProgress?.Invoke($"all jobs done: {chunkNo - 1} chunk(s), {fileItems.Count} files, {totalBytes / 1024.0 / 1024.0:F1} MB transferred");

        // Log an explicit status for every packaged folder and file: Copied, or Failed
        // when the job reported that specific file as failed OR the whole job ended in a
        // fatal error / timeout (in which case nothing in the job is confirmed imported,
        // so it must NOT be logged Copied — that would poison resume and delta baselines).
        foreach (var (jobId, emits) in jobEmits)
            foreach (var (type, src, tgt) in emits)
            {
                var rel = src.Length > sourceRoot.Length + 1 ? src[(sourceRoot.Length + 1)..] : src;
                var status = DecideEmitStatus(type, rel, jobId, failedJobs, failedRelPaths, out var reason);
                result.Add(type, src, tgt, status, reason);
            }

        // Hybrid tail: oversized files via streaming chunked upload.
        if (largeItems.Count > 0)
        {
            var targetRoot = targetList.RootFolder.ServerRelativeUrl;
            var fileCopier = new FileCopier(sourceCtx, targetCtx, users, _source.Rest, _target.Rest);
            foreach (var item in largeItems)
            {
                var fileRef = (string)item["FileRef"];
                var rel = fileRef[(sourceRoot.Length + 1)..];
                var parentDir = rel.Contains('/') ? $"{targetRoot}/{rel[..rel.LastIndexOf('/')]}" : targetRoot;
                OnProgress?.Invoke($"hybrid large file: {rel} ({DeclaredSize(item) / 1048576.0:F0} MB)");
                try
                {
                    await fileCopier.CopyLargeFileAsync(item, fileRef, parentDir, rel, options, result);
                }
                catch (Exception ex)
                {
                    result.Add("File", fileRef, parentDir, ItemCopyStatus.Failed, ex.Message);
                }
            }
            foreach (var (k, v) in fileCopier.SourceHashes) result.FileHashes[k] = v;
        }

        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// Emit status for one packaged item. It is Copied only when nothing marked it as
    /// failed: a per-file failure (failedRelPaths, files only) or a job that ended in a
    /// fatal error / timeout (failedJobs) both mean the item was NOT confirmed imported.
    /// Pure so it can be unit-tested without a tenant.
    /// </summary>
    public static ItemCopyStatus DecideEmitStatus(string type, string relPath, Guid jobId,
        IReadOnlySet<Guid> failedJobs, IReadOnlyDictionary<string, string> failedRelPaths, out string? reason)
    {
        if (type == "File" && TryMatchFailure(relPath, failedRelPaths, out var perFile))
        {
            reason = perFile;
            return ItemCopyStatus.Failed;
        }
        if (failedJobs.Contains(jobId))
        {
            reason = "the migration job did not complete (fatal error or timeout); item not confirmed imported";
            return ItemCopyStatus.Failed;
        }
        reason = null;
        return ItemCopyStatus.Copied;
    }

    /// <summary>
    /// Matches a packaged file's library-relative path against a per-file failure key.
    /// SPO names the file in its error message inconsistently (leaf name, library-relative
    /// path, or web-relative URL), so an exact-key lookup silently missed real failures and
    /// let a failed file be logged Copied. This matches by exact path, either-direction path
    /// suffix, or leaf name.
    /// </summary>
    public static bool TryMatchFailure(string relPath, IReadOnlyDictionary<string, string> failedRelPaths, out string reason)
    {
        reason = "";
        if (failedRelPaths.Count == 0) return false;
        if (failedRelPaths.TryGetValue(relPath, out var exact)) { reason = exact; return true; }

        var leaf = relPath.Contains('/') ? relPath[(relPath.LastIndexOf('/') + 1)..] : relPath;
        foreach (var (rawKey, msg) in failedRelPaths)
        {
            var key = rawKey.Replace('\\', '/').Trim('/');
            var keyLeaf = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
            if (key.Equals(relPath, StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("/" + relPath, StringComparison.OrdinalIgnoreCase)
                || relPath.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || keyLeaf.Equals(leaf, StringComparison.OrdinalIgnoreCase))
            {
                reason = msg;
                return true;
            }
        }
        return false;
    }

    /// <summary>Polls the shared queue (and job status as fallback) until every given job ends.</summary>
    private async Task WaitForJobsAsync(ClientContext targetCtx, string queueUri,
        IEnumerable<Guid> jobIds, Dictionary<Guid, byte[]> keys, CopyResult result,
        Dictionary<string, string> failedRelPaths, HashSet<Guid> failedJobs)
    {
        var waiting = new HashSet<Guid>(jobIds);
        // Idle timeout, not absolute: give up only after this long with NO progress, so a
        // large migration whose jobs legitimately take hours is not cut off at 40 min (which
        // would falsely fail every unfinished job and, per C4, mis-report their items).
        var maxIdle = TimeSpan.FromMinutes(40);
        var lastActivity = DateTime.UtcNow;
        while (waiting.Count > 0 && DateTime.UtcNow - lastActivity < maxIdle)
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            foreach (var (text, msgId, popReceipt) in await _azure.GetQueueMessagesAsync(queueUri))
            {
                lastActivity = DateTime.UtcNow;   // any message counts as progress
                var json = DecryptQueueMessage(text, keys);
                if (json == null) continue;       // not ours / corrupt: leave it to expire
                var handled = false;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var evt = doc.RootElement.TryGetProperty("Event", out var e) ? e.GetString() : null;
                    var jobId = doc.RootElement.TryGetProperty("JobId", out var j) && Guid.TryParse(j.GetString(), out var g) ? g : Guid.Empty;
                    if (evt is "JobError" or "JobFatalError" or "JobWarning")
                    {
                        var message = doc.RootElement.TryGetProperty("Message", out var m) ? m.GetString() : json;
                        // A per-file error names the file ("...name <rel> already exists/...").
                        // Record it so that file gets its own Failed row (with the reason)
                        // instead of a generic Job row. Job-level issues stay as a Job row.
                        var mt = evt != "JobWarning" && message != null
                            ? System.Text.RegularExpressions.Regex.Match(message, @"name\s+(.+?)\s+(?:already exists|could not|is|was)")
                            : System.Text.RegularExpressions.Match.Empty;
                        if (mt.Success)
                            failedRelPaths[mt.Groups[1].Value.Trim()] = message!;
                        else
                            result.Add("Job", jobId.ToString(), "", evt == "JobWarning" ? ItemCopyStatus.Warning : ItemCopyStatus.Failed, message);
                        // A fatal error aborts the whole job: none of its items are confirmed
                        // imported, so mark the job failed and fail its items in the emit loop.
                        if (evt == "JobFatalError" && jobId != Guid.Empty)
                            failedJobs.Add(jobId);
                    }
                    if (evt == "JobEnd")
                    {
                        waiting.Remove(jobId);
                        OnProgress?.Invoke($"job {jobId} ended ({waiting.Count} still running)");
                    }
                    handled = true;
                }
                catch { /* non-JSON payload: leave the message alone */ }
                // Delete a message we recorded so it is not re-read into duplicate rows, and
                // so a JobError is captured before the job-status fallback can drop the job.
                if (handled)
                    try { await _azure.DeleteQueueMessageAsync(queueUri, msgId, popReceipt); } catch { /* best-effort */ }
            }

            // Fallback: a job whose end event we missed.
            foreach (var jobId in waiting.ToList())
            {
                var state = targetCtx.Site.GetMigrationJobStatus(jobId);
                await targetCtx.ExecuteWithRetryAsync();
                if (state.Value == MigrationJobState.None)
                {
                    waiting.Remove(jobId);
                    lastActivity = DateTime.UtcNow;   // a job finishing is progress
                }
            }
        }

        foreach (var jobId in waiting)
        {
            failedJobs.Add(jobId);   // items in a timed-out job are not confirmed imported
            result.Add("Job", jobId.ToString(), "", ItemCopyStatus.Failed, "timed out waiting for the migration job");
        }
    }

    private static string? DecryptQueueMessage(string text, Dictionary<Guid, byte[]> keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Label", out var label) && label.GetString() == "Encrypted")
            {
                var jobId = Guid.TryParse(doc.RootElement.GetProperty("JobId").GetString(), out var g) ? g : Guid.Empty;
                if (!keys.TryGetValue(jobId, out var key)) return null;
                return System.Text.Encoding.UTF8.GetString(Aes256Cbc.Decrypt(
                    Convert.FromBase64String(doc.RootElement.GetProperty("Content").GetString()!),
                    key, doc.RootElement.GetProperty("IV").GetString()!));
            }
            return text;
        }
        catch
        {
            return text;
        }
    }

    private static string ParentDir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static IEnumerable<string> ExpandChain(string dir)
    {
        var parts = dir.Split('/');
        for (var i = 1; i <= parts.Length; i++)
            yield return string.Join('/', parts[..i]);
    }
}
