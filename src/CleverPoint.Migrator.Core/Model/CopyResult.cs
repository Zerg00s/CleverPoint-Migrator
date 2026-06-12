namespace CleverPoint.Migrator.Core.Model;

public enum ItemCopyStatus { Copied, Skipped, Warning, Failed }

/// <summary>One row of the migration log: a single item, file, or folder.</summary>
public class ItemCopyRecord
{
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string ItemType { get; set; } = "";   // Item | File | Folder | Field | View | List
    public ItemCopyStatus Status { get; set; }
    public string? Message { get; set; }
    public long SizeBytes { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>Aggregated outcome of one copy operation.</summary>
public class CopyResult
{
    public List<ItemCopyRecord> Records { get; } = new();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public int ThrottleHits { get; set; }

    /// <summary>Items found by the scan (post-filter); powers progress %/ETA.</summary>
    public int PlannedItems { get; set; }

    /// <summary>SHA-256 per source FileRef (library copies only), for verification.</summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>Source item id -> target item id pairs created by this run (persist via HistoryStore for delta upserts).</summary>
    public List<(int SourceId, int TargetId)> ItemMappings { get; } = new();

    /// <summary>
    /// Highest source Modified (UTC, server-stamped) seen during the scan.
    /// Use as the next delta's ModifiedSinceUtc: it is immune to client/server
    /// clock skew, which silently breaks wall-clock baselines.
    /// </summary>
    public DateTime? MaxSourceModifiedUtc { get; set; }

    public int Copied => Records.Count(r => r.Status == ItemCopyStatus.Copied);
    public int Skipped => Records.Count(r => r.Status == ItemCopyStatus.Skipped);
    public int Warnings => Records.Count(r => r.Status == ItemCopyStatus.Warning);
    public int Failed => Records.Count(r => r.Status == ItemCopyStatus.Failed);

    /// <summary>Raised for every record (live progress, history persistence, fault injection in tests).</summary>
    public event Action<ItemCopyRecord>? RecordAdded;

    public ItemCopyRecord Add(string itemType, string sourcePath, string targetPath, ItemCopyStatus status, string? message = null)
    {
        var rec = new ItemCopyRecord
        {
            ItemType = itemType, SourcePath = sourcePath, TargetPath = targetPath,
            Status = status, Message = message,
        };
        Records.Add(rec);
        RecordAdded?.Invoke(rec);
        return rec;
    }

    public string Summary() =>
        $"{Copied} copied, {Skipped} skipped, {Warnings} warnings, {Failed} failed" +
        (ThrottleHits > 0 ? $", {ThrottleHits} throttle hits" : "");
}
