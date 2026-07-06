using CleverPoint.Migrator.Core.Model;

namespace CleverPoint.Migrator.Ux.Services;

public enum CopyTaskState { Queued, Running, Completed, CompletedWithIssues, Failed, Cancelled }

/// <summary>One queued/running/finished copy, owned by the runner (not a page).</summary>
public class CopyTask
{
    public int Id { get; init; }
    public string Name { get; set; } = "";           // settable: tasks can be renamed
    public string SourceSite { get; init; } = "";
    public string TargetSite { get; init; } = "";
    public string SourceList { get; init; } = "";
    public bool SourceIsLibrary { get; init; }   // preserved so "Copy task" re-runs with the right scope
    public string Engine { get; init; } = "Classic";
    public CopyOptions Options { get; init; } = new();

    public long HistoryRunId { get; set; }
    public CopyTaskState State { get; set; } = CopyTaskState.Queued;
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Warnings { get; set; }
    public int Failed { get; set; }
    public int LogCount { get; set; }

    // Progress: Total is the scan's planned item count (files/folders/items); Processed
    // counts content rows seen so far. Percent drives the live progress bar. Total stays
    // 0 until the source scan finishes (and for the Migration API engine, which is phase-based).
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Percent => Total > 0 ? Math.Min(100, Processed * 100 / Total) : 0;

    /// <summary>Bytes transferred so far (sum of copied content sizes) for the live MB/s read-out.</summary>
    public long BytesProcessed { get; set; }

    /// <summary>Set each time SharePoint throttles a request (429/503). The Activity view shows a
    /// "throttling" chip while this is recent; ThrottleWaitSeconds is the last Retry-After wait.</summary>
    public DateTime? LastThrottleUtc { get; set; }
    public int ThrottleWaitSeconds { get; set; }
    public int ThrottleHits { get; set; }
    /// <summary>True when a throttle happened within the last few seconds (drives the live chip).</summary>
    public bool IsThrottling => LastThrottleUtc is { } t && (DateTime.UtcNow - t).TotalSeconds < 8;

    /// <summary>Live throughput (items/sec, bytes/sec, ETA) from processed/total over elapsed time.</summary>
    public Throughput.Stats Rate()
    {
        var start = StartedUtc;
        if (start is null || State != CopyTaskState.Running) return new Throughput.Stats(0, 0, null);
        return Throughput.Estimate(Processed, Total, BytesProcessed, DateTime.UtcNow - start.Value);
    }

    public string? Message { get; set; }
    public string? Phase { get; set; }   // live progress text (esp. for the Migration API engine)
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Rolling live log (capped) for the Activity view; history keeps the full record.</summary>
    public List<ItemCopyRecord> Log { get; } = new();
    public CancellationTokenSource Cts { get; } = new();

    public bool IsActive => State is CopyTaskState.Queued or CopyTaskState.Running;
}

/// <summary>
/// Runs migrations in the background, independent of any page, so navigating away
/// never cancels them. Runs up to MaxParallelMigrations at once and queues the
/// rest. The Activity page renders from here; the footer shows the active count.
/// </summary>
public class MigrationRunner
{
    private const int MaxLogKept = 3000;

    private readonly UxMigrationService _mig;
    private readonly UxSettings _settings;
    private readonly List<CopyTask> _tasks = new();
    private readonly object _gate = new();
    private int _seq;
    private int _running;
    private DateTime _lastRaise = DateTime.MinValue;

    public MigrationRunner(UxMigrationService mig, UxSettings settings)
    {
        _mig = mig;
        _settings = settings;
    }

    public event Action? Changed;

    public IReadOnlyList<CopyTask> Snapshot()
    {
        lock (_gate) return _tasks.OrderByDescending(t => t.Id).ToList();
    }

    public int RunningCount { get { lock (_gate) return _tasks.Count(t => t.State == CopyTaskState.Running); } }
    public int QueuedCount { get { lock (_gate) return _tasks.Count(t => t.State == CopyTaskState.Queued); } }
    public int ActiveCount { get { lock (_gate) return _tasks.Count(t => t.IsActive); } }

    public CopyTask Enqueue(string name, string sourceSite, string targetSite, string sourceList, CopyOptions options, string engine, bool sourceIsLibrary = false)
    {
        CopyTask task;
        lock (_gate)
        {
            task = new CopyTask
            {
                Id = ++_seq, Name = name, SourceSite = sourceSite, TargetSite = targetSite,
                SourceList = sourceList, SourceIsLibrary = sourceIsLibrary, Options = options, Engine = engine, QueuedUtc = DateTime.UtcNow,
            };
            _tasks.Add(task);
        }
        Raise(force: true);
        Pump();
        return task;
    }

    /// <summary>Signals every queued/running task to cancel (e.g. on app close).</summary>
    public void CancelAll()
    {
        lock (_gate)
            foreach (var t in _tasks)
                if (t.IsActive) { try { t.Cts.Cancel(); } catch { } }
    }

    public void Cancel(int id)
    {
        CopyTask? t;
        lock (_gate) t = _tasks.FirstOrDefault(x => x.Id == id);
        if (t is null) return;
        t.Cts.Cancel();
        // A task still Queued never started, so mark it Cancelled now instead of letting the
        // pump start it (which would create a spurious history run just to stop it).
        lock (_gate)
            if (t.State == CopyTaskState.Queued)
            {
                t.State = CopyTaskState.Cancelled;
                t.FinishedUtc = DateTime.UtcNow;
            }
        Raise(force: true);
    }

    /// <summary>Rename a task (in-memory display); returns its history run id (0 if none yet).</summary>
    public long Rename(int id, string newName)
    {
        CopyTask? t;
        lock (_gate) t = _tasks.FirstOrDefault(x => x.Id == id);
        if (t is null) return 0;
        t.Name = newName;
        Raise(force: true);
        return t.HistoryRunId;
    }

    public void ClearFinished()
    {
        lock (_gate) _tasks.RemoveAll(t => !t.IsActive);
        Raise(force: true);
    }

    private void Pump()
    {
        List<CopyTask> toStart = new();
        lock (_gate)
        {
            var max = Math.Clamp(_settings.MaxParallelMigrations, 1, 3);
            while (_running < max)
            {
                var next = _tasks.FirstOrDefault(t => t.State == CopyTaskState.Queued);
                if (next is null) break;
                next.State = CopyTaskState.Running;
                next.StartedUtc = DateTime.UtcNow;
                _running++;
                toStart.Add(next);
            }
        }
        foreach (var t in toStart) _ = RunAsync(t);
    }

    private async Task RunAsync(CopyTask t)
    {
        Raise(force: true);
        try
        {
            var (result, status) = await _mig.RunAsync(
                t.SourceSite, t.TargetSite, t.SourceList, t.Options,
                rec => AddRecord(t, rec), t.Cts.Token, t.Engine, t.Name,
                onRunStarted: id => t.HistoryRunId = id,
                onPhase: msg => { t.Phase = msg; Raise(force: true); },
                onTotal: n => { if (n > t.Total) { t.Total = n; Raise(force: true); } },
                onThrottle: wait =>
                {
                    t.LastThrottleUtc = DateTime.UtcNow;
                    t.ThrottleWaitSeconds = wait;
                    t.ThrottleHits++;
                    Raise(force: true);
                },
                sourceIsLibrary: t.SourceIsLibrary);
            t.Copied = result.Copied; t.Skipped = result.Skipped;
            t.Warnings = result.Warnings; t.Failed = result.Failed;
            t.State = status switch
            {
                "Completed" => CopyTaskState.Completed,
                "Failed" => CopyTaskState.Failed,
                "Interrupted" => CopyTaskState.Cancelled,
                _ => CopyTaskState.CompletedWithIssues,
            };
            t.Message = $"{result.Copied} copied, {result.Skipped} skipped, {result.Warnings} warnings, {result.Failed} failed.";
        }
        catch (OperationCanceledException)
        {
            t.State = CopyTaskState.Cancelled;
            t.Message = "Cancelled.";
        }
        catch (Exception ex)
        {
            t.State = CopyTaskState.Failed;
            t.Message = ex.Message;
        }
        finally
        {
            t.FinishedUtc = DateTime.UtcNow;
            lock (_gate) _running--;
            Raise(force: true);
            Pump();
        }
    }

    private void AddRecord(CopyTask t, ItemCopyRecord rec)
    {
        lock (t.Log)
        {
            t.Log.Add(rec);
            if (t.Log.Count > MaxLogKept) t.Log.RemoveRange(0, t.Log.Count - MaxLogKept);
        }
        t.LogCount++;
        // Count only content rows toward progress (skip schema/progress rows like
        // Field, View, List, Site column, Progress) so Processed lines up with Total.
        if (rec.ItemType is "Item" or "File" or "Folder" or "OneNote")
        {
            t.Processed++;
            if (rec.SizeBytes > 0) t.BytesProcessed += rec.SizeBytes;
        }
        Raise();
    }

    private void Raise(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRaise).TotalMilliseconds < 250) return;
        _lastRaise = now;
        Changed?.Invoke();
    }
}
