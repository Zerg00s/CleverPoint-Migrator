using CleverPoint.Migrator.Core.History;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>Thin façade over the engine's real SQLite history store (Core).</summary>
public class HistoryService
{
    /// <summary>At startup, mark any run left "Running" by a previous session as
    /// Interrupted (jobs run in-process, so none can still be running here).</summary>
    public void ReconcileOrphanedRuns()
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            store.MarkRunningAsInterrupted();
        }
        catch { /* non-fatal */ }
    }

    public List<MigrationRun> GetRuns(int limit = 2000)
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            return store.GetRuns(limit);
        }
        catch
        {
            return new List<MigrationRun>();
        }
    }

    public MigrationRun? GetRun(long runId)
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            return store.GetRun(runId);
        }
        catch
        {
            return null;
        }
    }

    public List<(string ItemType, string SourcePath, string TargetPath, string Status, string? Message, string? ItemUrl, DateTime? WhenUtc)> GetItems(long runId)
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            return store.GetItems(runId);
        }
        catch
        {
            return new();
        }
    }

    public void RenameRun(long runId, string newName)
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            store.RenameRun(runId, newName);
        }
        catch { /* best-effort */ }
    }

    public void DeleteRuns(IEnumerable<long> ids)
    {
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            store.DeleteRuns(ids);
        }
        catch { /* best-effort */ }
    }
}
