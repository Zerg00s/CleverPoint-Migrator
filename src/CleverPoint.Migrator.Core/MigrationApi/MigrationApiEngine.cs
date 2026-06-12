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

        var schema = new SchemaCopier(sourceCtx, targetCtx);
        var targetList = await schema.CopyAsync(sourceList, options, result);
        targetCtx.Load(targetCtx.Web, w => w.Id, w => w.ServerRelativeUrl, w => w.Url);
        targetCtx.Load(targetList, l => l.Id, l => l.RootFolder.UniqueId, l => l.RootFolder.ServerRelativeUrl);
        await targetCtx.ExecuteQueryAsync();

        var itemCopier = new ItemCopier(sourceCtx, targetCtx, users);
        var copyFields = await itemCopier.GetCopyFieldsAsync(sourceList, targetList);
        var allItems = await itemCopier.LoadAllItemsAsync(sourceList, options);
        await users.PreResolveAsync(ItemCopier.CollectUserIds(allItems, copyFields));

        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var listLeaf = targetList.RootFolder.ServerRelativeUrl[(targetCtx.Web.ServerRelativeUrl.TrimEnd('/').Length + 1)..];
        var textFieldNames = copyFields.Where(f => f.TypeAsString is "Text" or "Note" or "Choice")
            .Select(f => f.InternalName).ToList();

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
        long DeclaredSize(ListItem i) =>
            long.TryParse(i.FieldValues.GetValueOrDefault("File_x0020_Size")?.ToString(), out var s) ? s : 0;
        var fileItems = allItems.Where(i => i.FileSystemObjectType == FileSystemObjectType.File
            && DeclaredSize(i) < options.LargeFileThresholdBytes).ToList();
        var largeItems = allItems.Where(i => i.FileSystemObjectType == FileSystemObjectType.File
            && DeclaredSize(i) >= options.LargeFileThresholdBytes).ToList();

        var folderMeta = new List<PackageFolder>();
        foreach (var item in folderItems)
        {
            var rel = ((string)item["FileRef"])[(sourceRoot.Length + 1)..];
            folderMeta.Add(new PackageFolder
            {
                LibraryRelativePath = rel,
                CreatedUtc = Operations.ItemCopier.ReadUtc(item["Created"]),
                ModifiedUtc = Operations.ItemCopier.ReadUtc(item["Modified"]),
                AuthorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Author")),
                EditorMapId = await MapUserAsync(item.FieldValues.GetValueOrDefault("Editor")),
            });
        }
        var allFolderPaths = folderMeta.Select(f => f.LibraryRelativePath)
            .Concat(fileItems.Select(i => ParentDir(((string)i["FileRef"])[(sourceRoot.Length + 1)..])).Where(d => d.Length > 0))
            .SelectMany(ExpandChain).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Count(c => c == '/')).ToList();
        foreach (var path in allFolderPaths)
            folderIds[path] = Guid.NewGuid();

        // ---- Provision the shared queue once ----
        var queue = targetCtx.Site.ProvisionMigrationQueue();
        await targetCtx.ExecuteQueryAsync();
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

        async Task<Guid> SubmitChunkAsync(MigrationPackageBuilder builder, List<ListItem> chunkFiles, int chunkNo)
        {
            // Fresh containers per job (Manifest.xml must sit at container root).
            var containers = targetCtx.Site.ProvisionMigrationContainers();
            await targetCtx.ExecuteQueryAsync();
            var key = containers.Value.EncryptionKey;

            // Stream the chunk's files one at a time.
            foreach (var item in chunkFiles)
            {
                var fileRef = (string)item["FileRef"];
                var rel = fileRef[(sourceRoot.Length + 1)..];
                var blobName = $"{Guid.NewGuid()}.dat";

                var file = sourceCtx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl(fileRef));
                var stream = file.OpenBinaryStream();
                await sourceCtx.ExecuteQueryAsync();
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
                        packageFile.TextFields[fieldName] = s;
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
            await targetCtx.ExecuteQueryAsync();
            pendingJobs[jobId.Value] = key;
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
        await WaitForJobsAsync(targetCtx, queueUri, new[] { firstJob }, pendingJobs, result);

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
            await WaitForJobsAsync(targetCtx, queueUri, laterJobs, pendingJobs, result);

        OnProgress?.Invoke($"all jobs done: {chunkNo - 1} chunk(s), {fileItems.Count} files, {totalBytes / 1024.0 / 1024.0:F1} MB transferred");

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

    /// <summary>Polls the shared queue (and job status as fallback) until every given job ends.</summary>
    private async Task WaitForJobsAsync(ClientContext targetCtx, string queueUri,
        IEnumerable<Guid> jobIds, Dictionary<Guid, byte[]> keys, CopyResult result)
    {
        var waiting = new HashSet<Guid>(jobIds);
        var deadline = DateTime.UtcNow.AddMinutes(40);
        while (waiting.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            foreach (var (text, _, _) in await _azure.GetQueueMessagesAsync(queueUri))
            {
                var json = DecryptQueueMessage(text, keys);
                if (json == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var evt = doc.RootElement.TryGetProperty("Event", out var e) ? e.GetString() : null;
                    var jobId = doc.RootElement.TryGetProperty("JobId", out var j) && Guid.TryParse(j.GetString(), out var g) ? g : Guid.Empty;
                    if (evt is "JobError" or "JobFatalError" or "JobWarning")
                    {
                        var message = doc.RootElement.TryGetProperty("Message", out var m) ? m.GetString() : json;
                        result.Add("Job", jobId.ToString(), "", evt == "JobWarning" ? ItemCopyStatus.Warning : ItemCopyStatus.Failed, message);
                    }
                    if (evt == "JobEnd")
                    {
                        waiting.Remove(jobId);
                        OnProgress?.Invoke($"job {jobId} ended ({waiting.Count} still running)");
                    }
                }
                catch { /* non-JSON payload */ }
            }

            // Fallback: a job whose end event we missed.
            foreach (var jobId in waiting.ToList())
            {
                var state = targetCtx.Site.GetMigrationJobStatus(jobId);
                await targetCtx.ExecuteQueryAsync();
                if (state.Value == MigrationJobState.None)
                    waiting.Remove(jobId);
            }
        }

        foreach (var jobId in waiting)
            result.Add("Job", jobId.ToString(), "", ItemCopyStatus.Failed, "timed out waiting for the migration job");
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
