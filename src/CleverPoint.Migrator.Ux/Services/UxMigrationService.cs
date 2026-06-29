using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Runs a real migration through the Core engine and records it to the SQLite
/// history store, streaming each item record to the caller for a live log.
/// App + certificate connections run fully headless here.
/// </summary>
public class UxMigrationService
{
    private readonly UxSettings _settings;
    private readonly BrowserSignIn _browser;
    public UxMigrationService(UxSettings settings, BrowserSignIn browser)
    {
        _settings = settings;
        _browser = browser;
    }

    public bool CanRun(string sourceSite, string targetSite, out string? why)
    {
        why = null;
        var s = UxConnectionResolver.Find(_settings, sourceSite);
        var t = UxConnectionResolver.Find(_settings, targetSite);
        if (s is null || t is null) { why = "Both the source and target need a saved connection."; return false; }
        if (!UxConnectionResolver.CanResolve(s, _browser) || !UxConnectionResolver.CanResolve(t, _browser))
        {
            why = "Sign in to both connections first (Connect on the Connections page or the explorer).";
            return false;
        }
        return true;
    }

    /// <summary>Runs one list/library copy. Returns the final result and status.</summary>
    public async Task<(CopyResult Result, string Status)> RunAsync(
        string sourceSite, string targetSite, string sourceListTitle, CopyOptions options,
        Action<ItemCopyRecord> onRecord, CancellationToken ct, string engine = "Classic", string? runName = null,
        Action<long>? onRunStarted = null, Action<string>? onPhase = null, Action<int>? onTotal = null)
    {
        var sConn = UxConnectionResolver.Find(_settings, sourceSite)!;
        var tConn = UxConnectionResolver.Find(_settings, targetSite)!;
        var source = UxConnectionResolver.Resolve(sConn, sourceSite, _browser);
        var target = UxConnectionResolver.Resolve(tConn, targetSite, _browser);

        using var store = new HistoryStore(UxSettings.HistoryDbPath);
        var runId = store.StartRun(new MigrationRun
        {
            Name = string.IsNullOrWhiteSpace(runName) ? $"{sourceListTitle} -> {options.TargetListTitle}" : runName,
            SourceUrl = sourceSite, SourceList = sourceListTitle,
            TargetUrl = targetSite, TargetList = options.TargetListTitle,
            Engine = engine == "MigrationApi" ? "MigrationApi" : "Classic",
            // Remember the exact source scope so the run can be re-opened in the
            // explorer later (folder + the picked files/folders/list items).
            ScopeJson = System.Text.Json.JsonSerializer.Serialize(new RunScope
            {
                SourceFolder = options.SourceFolderServerRelativeUrl,
                TargetListUrl = options.TargetListUrl,
                TargetSubfolder = options.TargetSubfolderRelative,
                SelectedPaths = options.SelectedPaths,
                ItemIds = options.ItemIds,
            }),
        });
        onRunStarted?.Invoke(runId);

        var result = new CopyResult();
        var gate = new object();
        result.RecordAdded += rec =>
        {
            lock (gate) store.RecordItem(runId, rec);
            // The scan sets PlannedItems before content rows stream; surface it for the
            // live progress bar (idempotent: the runner only grows the total).
            if (onTotal != null && result.PlannedItems > 0) onTotal(result.PlannedItems);
            onRecord(rec);
        };

        // Reuse the source->target item id map from prior runs so a re-copy of a
        // generic LIST updates/skips existing items (per ExistingMode) instead of
        // duplicating them. Libraries key off file path and ignore this.
        var pairKey = HistoryStore.PairKey(sourceSite, sourceListTitle, targetSite, options.TargetListTitle);
        if (options.UpsertItemMap == null)
        {
            var prior = store.GetItemMap(pairKey);
            if (prior.Count > 0) options.UpsertItemMap = prior;
        }

        var status = "Completed";
        try
        {
            if (engine == "MigrationApi")
            {
                // The Migration API engine returns its records at the end; replay
                // them into the live log + history (it doesn't stream like Classic).
                var apiEngine = new Core.MigrationApi.MigrationApiEngine(source, target);
                // The API engine returns its item records only at the end, so without
                // this the live log is empty for the whole (long) run. Surface its phase
                // messages both as the task's headline phase AND as Info log rows so the
                // user sees a running activity feed (reading, packaging, jobs, etc.).
                apiEngine.OnProgress += msg =>
                {
                    onPhase?.Invoke(msg);
                    result.Add("Progress", msg, "", ItemCopyStatus.Info);
                };
                var apiResult = await Task.Run(() => apiEngine.CopyLibraryAsync(sourceListTitle, options), ct);
                foreach (var rec in apiResult.Records)
                    result.Add(rec.ItemType, rec.SourcePath, rec.TargetPath, rec.Status, rec.Message);
                if (result.Failed > 0) status = "CompletedWithIssues";
            }
            else
            {
                await Task.Run(() => CopyEngine.CopyListAsync(source, target, sourceListTitle, options, null, ct, result), ct);
                if (result.Failed > 0) status = "CompletedWithIssues";
            }
        }
        catch (OperationCanceledException)
        {
            status = "Interrupted";
            result.Add("Run", sourceListTitle, "", ItemCopyStatus.Skipped, "cancelled by the user");
        }
        catch (Exception ex)
        {
            status = "Failed";
            result.Add("Run", sourceListTitle, "", ItemCopyStatus.Failed, $"run stopped: {ex.Message}");
        }
        finally
        {
            // Persist new id mappings so the next copy of this list can update in place.
            if (result.ItemMappings.Count > 0)
                store.SaveItemMap(pairKey, result.ItemMappings);
            store.FinishRun(runId, result, status);
        }
        return (result, status);
    }
}
