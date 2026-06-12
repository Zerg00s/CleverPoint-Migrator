using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Operations;

public class HealingOptions
{
    /// <summary>Re-run incrementals automatically until clean (off by default).</summary>
    public bool AutoRetry { get; set; }

    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Detect corrupt target files (0-byte or under half the source size),
    /// delete them (only files WE migrated) and re-copy. Off by default.
    /// </summary>
    public bool RepairCorruptFiles { get; set; }

    /// <summary>A target file under this fraction of the source size counts as corrupt.</summary>
    public double MinSizeRatio { get; set; } = 0.5;
}

/// <summary>
/// Wraps a copy with the self-healing loop: run, analyze (failures +
/// corrupt-file scan), then re-run targeted incrementals until clean or the
/// retry budget is spent. Every retry only touches what needs fixing.
/// </summary>
public static class RunCoordinator
{
    public static async Task<CopyResult> RunWithHealingAsync(
        SpConnection source, SpConnection target, string sourceListTitle, CopyOptions options,
        HealingOptions healing, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var overall = await CopyEngine.CopyListAsync(source, target, sourceListTitle, options, null, ct);
        if (!healing.AutoRetry && !healing.RepairCorruptFiles) return overall;
        return await HealAsync(source, target, sourceListTitle, options, healing, overall, onProgress, ct);
    }

    /// <summary>
    /// Analyze-and-heal an EXISTING migration without a fresh full copy:
    /// scans for failed records and corrupt target files, then re-runs
    /// targeted incrementals until clean or the retry budget is spent.
    /// </summary>
    public static async Task<CopyResult> HealAsync(
        SpConnection source, SpConnection target, string sourceListTitle, CopyOptions options,
        HealingOptions healing, CopyResult? previousResult = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var overall = previousResult ?? new CopyResult();

        for (var attempt = 1; attempt <= healing.MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var failedPaths = overall.Records
                .Where(r => r.Status == ItemCopyStatus.Failed && r.SourcePath.Length > 0)
                .Select(r => r.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var corruptPaths = healing.RepairCorruptFiles
                ? await FindCorruptFilesAsync(source, target, sourceListTitle, options.TargetListTitle, healing, onProgress)
                : new List<(string SourceRef, string TargetRef)>();

            if (failedPaths.Count == 0 && corruptPaths.Count == 0)
            {
                onProgress?.Invoke($"healing: clean after {attempt - 1} retr{(attempt - 1 == 1 ? "y" : "ies")}");
                return overall;
            }
            if (!healing.AutoRetry && failedPaths.Count > 0)
                onProgress?.Invoke($"healing: {failedPaths.Count} failed item(s) found but auto-retry is off");

            onProgress?.Invoke($"healing attempt {attempt}: {failedPaths.Count} failed, {corruptPaths.Count} corrupt");

            // Delete corrupt targets (only ones we own) so the re-copy is clean.
            if (corruptPaths.Count > 0)
            {
                using var targetCtx = target.CreateContext();
                foreach (var (_, targetRef) in corruptPaths)
                {
                    try
                    {
                        targetCtx.Web.GetFileByServerRelativeUrl(targetRef).DeleteObject();
                        await targetCtx.ExecuteQueryAsync();
                        onProgress?.Invoke($"healing: deleted corrupt target {targetRef.Split('/')[^1]}");
                    }
                    catch (Exception ex)
                    {
                        onProgress?.Invoke($"healing: could not delete {targetRef}: {ex.Message}");
                    }
                }
            }

            // Targeted re-run: skip every healthy source path.
            var needsCopy = new HashSet<string>(
                failedPaths.Concat(corruptPaths.Select(c => c.SourceRef)), StringComparer.OrdinalIgnoreCase);
            var skipHealthy = await AllSourcePathsAsync(source, sourceListTitle, options);
            skipHealthy.RemoveWhere(p => needsCopy.Contains(p));

            var retryOptions = CloneForRetry(options);
            retryOptions.ResumeSkipPaths = skipHealthy;
            var retry = await CopyEngine.CopyListAsync(source, target, sourceListTitle, retryOptions, null, ct);

            foreach (var rec in retry.Records.Where(r => r.Status != ItemCopyStatus.Skipped))
                overall.Add(rec.ItemType, rec.SourcePath, rec.TargetPath, rec.Status,
                    $"healing #{attempt}: {rec.Message ?? rec.Status.ToString()}");

            // Failures fixed by this retry stop counting against the next pass.
            if (retry.Failed == 0)
                foreach (var rec in overall.Records.Where(r => r.Status == ItemCopyStatus.Failed && needsCopy.Contains(r.SourcePath)).ToList())
                    rec.Status = ItemCopyStatus.Warning;
        }

        onProgress?.Invoke("healing: retry budget exhausted; remaining issues are in the log");
        return overall;
    }

    /// <summary>Target files that are missing, 0-byte, or far smaller than their source.</summary>
    public static async Task<List<(string SourceRef, string TargetRef)>> FindCorruptFilesAsync(
        SpConnection source, SpConnection target, string sourceListTitle, string targetListTitle,
        HealingOptions healing, Action<string>? onProgress = null)
    {
        using var sourceCtx = source.CreateContext();
        using var targetCtx = target.CreateContext();
        var sourceSizes = await LoadFileSizesAsync(sourceCtx, sourceListTitle);
        var targetSizes = await LoadFileSizesAsync(targetCtx, targetListTitle);

        var corrupt = new List<(string, string)>();
        foreach (var (rel, (sourceRef, sourceSize)) in sourceSizes)
        {
            if (!targetSizes.TryGetValue(rel, out var t))
                continue;   // missing entirely = a Failed record, handled by retry
            if (sourceSize > 0 && (t.Size == 0 || t.Size < sourceSize * healing.MinSizeRatio))
            {
                onProgress?.Invoke($"healing: {rel} looks corrupt (source {sourceSize} B, target {t.Size} B)");
                corrupt.Add((sourceRef, t.Ref));
            }
        }
        return corrupt;
    }

    private static async Task<Dictionary<string, (string Ref, long Size)>> LoadFileSizesAsync(ClientContext ctx, string listTitle)
    {
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = list.RootFolder.ServerRelativeUrl;

        var sizes = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>500</RowLimit></View>" };
        do
        {
            var page = list.GetItems(query);
            ctx.Load(page);
            ctx.Load(page, p => p.Include(i => i.FileSystemObjectType), p => p.ListItemCollectionPosition);
            await ctx.ExecuteQueryAsync();
            foreach (var item in page)
            {
                if (item.FileSystemObjectType != FileSystemObjectType.File) continue;
                var fileRef = (string)item["FileRef"];
                var size = long.TryParse(item.FieldValues.GetValueOrDefault("File_x0020_Size")?.ToString(), out var s) ? s : 0;
                sizes[fileRef[(root.Length + 1)..]] = (fileRef, size);
            }
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return sizes;
    }

    private static async Task<HashSet<string>> AllSourcePathsAsync(SpConnection source, string listTitle, CopyOptions options)
    {
        using var ctx = source.CreateContext();
        var sizes = await LoadFileSizesAsync(ctx, listTitle);
        return new HashSet<string>(sizes.Values.Select(v => v.Item1), StringComparer.OrdinalIgnoreCase);
    }

    private static CopyOptions CloneForRetry(CopyOptions options) => new()
    {
        TargetListTitle = options.TargetListTitle,
        TargetListUrl = options.TargetListUrl,
        CopyViews = false,
        CopyListSettings = false,
        PreserveAuthorsAndDates = options.PreserveAuthorsAndDates,
        CopyPermissions = options.CopyPermissions,
        CopyAttachments = options.CopyAttachments,
        UnresolvedUserFallback = options.UnresolvedUserFallback,
        LargeFileThresholdBytes = options.LargeFileThresholdBytes,
        UploadSliceBytes = options.UploadSliceBytes,
        RecordSkippedItems = false,
    };
}
