using CleverPoint.Migrator.Core.History;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>Thin façade over the engine's real SQLite history store (Core).</summary>
public class HistoryService
{
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
