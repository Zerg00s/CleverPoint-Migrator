using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using CleverPoint.Migrator.Core.Validation;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>
/// Runs a real migration through the Core engine and records it to the SQLite
/// history store, streaming each item record to the caller for a live log.
/// App + certificate connections run fully headless here.
/// </summary>
public class UxMigrationService
{
    private readonly UxSettings _settings;
    private readonly BrowserSignIn _browser;

    /// <summary>
    /// False for the command line and other unattended hosts: an expired sign-in then fails the run
    /// with a sign-in-required error and never opens a browser.
    /// </summary>
    public bool AllowInteractiveSignIn { get; set; } = true;

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

    /// <summary>
    /// Makes sure a connection is signed in before work starts. A browser (cookie) connection whose
    /// captured session is missing or expired opens the sign-in dialog for that specific tenant and
    /// waits. A Microsoft sign-in connection renews silently and opens the browser only when the
    /// cached sign-in can no longer be renewed. Cert connections need nothing.
    /// Throws a clear, tenant-named error if the user does not complete the sign-in.
    /// </summary>
    private async Task EnsureSignedInAsync(SavedConnection c, string siteUrl)
    {
        if (UxConnectionResolver.IsCert(c)) return;
        if (UxConnectionResolver.IsDelegated(c))
        {
            if (!AllowInteractiveSignIn)
            {
                // Proves the cached sign-in still renews; throws a sign-in-required error otherwise.
                if (!UxConnectionResolver.IsSignedIn(c))
                    throw new Core.Auth.SignInRequiredException($"Not signed in to {HostOf(siteUrl)} ({c.Name}).");
                await UxConnectionResolver.DelegatedProvider(c).GetTokenAsync(new Uri(siteUrl).Host);
                return;
            }
            if (await UxConnectionResolver.EnsureDelegatedAsync(c, siteUrl)) _settings.Save();
            return;
        }
        if (_browser.HasSession(siteUrl)) return;               // still valid
        if (!AllowInteractiveSignIn)
            throw new Core.Auth.SignInRequiredException($"The browser sign-in for {HostOf(siteUrl)} ({c.Name}) has expired.");
        if (!_browser.Available)                                 // browser auth is Windows-only
            throw new InvalidOperationException($"Sign-in for {HostOf(siteUrl)} ({c.Name}) has expired and can only be renewed on Windows.");
        var (ok, msg) = await _browser.SignInAsync(siteUrl, fresh: false);
        if (!ok)
            throw new InvalidOperationException($"Sign-in required for {HostOf(siteUrl)} ({c.Name}): {msg}");
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    /// <summary>
    /// Runs a source-vs-target verification for a completed run and writes a client-ready HTML
    /// report to Downloads. Metadata-only by default (fast, honest verdict); deep=true also
    /// hashes a 1-in-10 sample of file contents. Returns the report and the HTML path.
    /// onProgress receives short phase text ("Reading source list… 12,500 items") for the UI;
    /// ct cancels between pages/items and throws OperationCanceledException.
    /// </summary>
    public async Task<(CompareReport Report, string HtmlPath)> VerifyRunAsync(MigrationRun run, bool deep, string? clientName = null,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        onProgress?.Invoke("Checking sign-in…");
        var sConn = UxConnectionResolver.Find(_settings, run.SourceUrl)
            ?? throw new InvalidOperationException($"No saved connection for the source {HostOf(run.SourceUrl)}.");
        var tConn = UxConnectionResolver.Find(_settings, run.TargetUrl)
            ?? throw new InvalidOperationException($"No saved connection for the target {HostOf(run.TargetUrl)}.");
        await EnsureSignedInAsync(sConn, run.SourceUrl);
        await EnsureSignedInAsync(tConn, run.TargetUrl);
        var source = UxConnectionResolver.Resolve(sConn, run.SourceUrl, _browser);
        var target = UxConnectionResolver.Resolve(tConn, run.TargetUrl, _browser);

        // Generic lists pair by the persisted id map; libraries pair by path and ignore it.
        Dictionary<int, int>? itemMap = null;
        try
        {
            using var store = new HistoryStore(UxSettings.HistoryDbPath);
            var map = store.GetItemMap(HistoryStore.PairKey(run.SourceUrl, run.SourceList, run.TargetUrl, run.TargetList));
            if (map.Count > 0) itemMap = map;
        }
        catch { /* no map is fine for libraries */ }

        var report = await CompareReport.RunAsync(source, target, run.SourceList, run.TargetList,
            Array.Empty<string>(), compareContent: deep, contentSampleEvery: 10, itemMap: itemMap,
            onProgress: onProgress, ct: ct);
        onProgress?.Invoke("Writing the report…");
        report.PreparedBy = "CleverPoint Migrator";
        report.ClientName = clientName;
        report.ToolVersion = (System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly()).GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

        var path = LogExporter.DownloadsPath($"Verification - {run.SourceList} to {run.TargetList}", "csv");
        report.ExportCsv(path);
        return (report, path);
    }

    /// <summary>Runs one list/library copy. Returns the final result and status.</summary>
    public async Task<(CopyResult Result, string Status)> RunAsync(
        string sourceSite, string targetSite, string sourceListTitle, CopyOptions options,
        Action<ItemCopyRecord> onRecord, CancellationToken ct, string engine = "Classic", string? runName = null,
        Action<long>? onRunStarted = null, Action<string>? onPhase = null, Action<int>? onTotal = null,
        Action<int>? onThrottle = null, bool sourceIsLibrary = false,
        string? jobId = null, IdentityMapping? mappingOverride = null)
    {
        // A cancel can also arrive from another process (the command line), as a flag in the history
        // database that the heartbeat below picks up.
        using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = cancelSource.Token;
        // The Migration API engine is fast but limited. When this copy asks for something it cannot do,
        // the Classic engine does the work instead, and the run log says why. Doing less than was
        // asked without saying so is never an option.
        string? engineNote = null;
        if (engine == "MigrationApi")
        {
            var reasons = Core.MigrationApi.MigrationApiEngine.ReasonsToUseClassic(options, sourceIsLibrary);
            if (reasons.Count > 0)
            {
                engine = "Classic";
                engineNote = "Using the Classic engine instead of the Migration API engine, because " + string.Join("; ", reasons) + ".";
            }
        }
        var sConn = UxConnectionResolver.Find(_settings, sourceSite)!;
        var tConn = UxConnectionResolver.Find(_settings, targetSite)!;
        // If a sign-in has expired, ask for it again for THAT tenant before the copy starts, so
        // the run doesn't just fail. The browser helper window is titled "Sign in - <host>", so it
        // is clear which tenant is being re-authenticated.
        await EnsureSignedInAsync(sConn, sourceSite);
        await EnsureSignedInAsync(tConn, targetSite);
        var source = UxConnectionResolver.Resolve(sConn, sourceSite, _browser);
        var target = UxConnectionResolver.Resolve(tConn, targetSite, _browser);

        // Apply a saved source->target identity mapping (users, groups, orphan fallback) if one
        // exists for this tenant pair. The engine remaps Author/Editor and user/group fields.
        var mapping = mappingOverride ?? new UxMappingStore().Load(sourceSite, targetSite);
        Dictionary<string, string>? userMap = mapping.Users.Count > 0 ? mapping.Users : null;
        Dictionary<string, string>? groupMap = mapping.Groups.Count > 0 ? mapping.Groups : null;
        if (string.IsNullOrEmpty(options.UnresolvedUserFallback) && !string.IsNullOrEmpty(mapping.OrphanFallbackLogin))
            options.UnresolvedUserFallback = mapping.OrphanFallbackLogin;

        // Surface SharePoint throttling (429/503 with Retry-After) to the live UI. Both the
        // source (downloads) and target (uploads) REST clients can be throttled; report the
        // wait seconds so the Activity view can show a "throttling" state.
        Action<string, int, int>? throttleHandler = null;
        if (onThrottle != null)
        {
            throttleHandler = (_, wait, _) => onThrottle(wait);
            source.Rest.OnThrottle += throttleHandler;
            if (!ReferenceEquals(target.Rest, source.Rest)) target.Rest.OnThrottle += throttleHandler;
        }

        using var store = new HistoryStore(UxSettings.HistoryDbPath);
        var runId = store.StartRun(new MigrationRun
        {
            Name = string.IsNullOrWhiteSpace(runName) ? $"{sourceListTitle} -> {options.TargetListTitle}" : runName,
            SourceUrl = sourceSite, SourceList = sourceListTitle,
            TargetUrl = targetSite, TargetList = options.TargetListTitle,
            JobId = jobId,
            Engine = engine == "MigrationApi" ? "MigrationApi" : "Classic",
            // Remember the exact source scope so the run can be re-opened in the
            // explorer later (folder + the picked files/folders/list items).
            // The wizard settings ride along so "Run again" restores the same choices.
            ScopeJson = System.Text.Json.JsonSerializer.Serialize(
                RunScope.FromOptions(options, engine == "MigrationApi" ? "MigrationApi" : "Classic", sourceIsLibrary)),
        });
        onRunStarted?.Invoke(runId);

        var result = new CopyResult();
        var gate = new object();
        // Every few seconds: tell other processes this run is alive, publish the planned total for
        // their progress display, and stop when one of them asked for a cancel.
        using var heartbeat = new Timer(_ =>
        {
            try
            {
                bool cancel;
                lock (gate) cancel = store.Heartbeat(runId, result.PlannedItems);
                if (cancel) cancelSource.Cancel();
            }
            catch { /* a busy database or a finished run must not take the run down */ }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        result.RecordAdded += rec =>
        {
            lock (gate) store.RecordItem(runId, rec);
            // The scan sets PlannedItems before content rows stream; surface it for the
            // live progress bar (idempotent: the runner only grows the total).
            if (onTotal != null && result.PlannedItems > 0) onTotal(result.PlannedItems);
            onRecord(rec);
        };

        if (engineNote != null) result.Add("Run", sourceListTitle, options.TargetListTitle, ItemCopyStatus.Info, engineNote);

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
                var apiResult = await Task.Run(() => apiEngine.CopyLibraryAsync(sourceListTitle, options, userMap, ct), ct);
                foreach (var rec in apiResult.Records)
                    result.Add(rec.ItemType, rec.SourcePath, rec.TargetPath, rec.Status, rec.Message);
                if (result.Failed > 0) status = "CompletedWithIssues";
            }
            else
            {
                await Task.Run(() => CopyEngine.CopyListAsync(source, target, sourceListTitle, options, userMap, ct, result, groupMap), ct);
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
            // Detach the throttle handler so a shared REST client isn't left holding a
            // reference to this run's callback after it finishes.
            if (throttleHandler != null)
            {
                source.Rest.OnThrottle -= throttleHandler;
                if (!ReferenceEquals(target.Rest, source.Rest)) target.Rest.OnThrottle -= throttleHandler;
            }
            // Stop the heartbeat before the last writes; it shares this connection.
            heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
            lock (gate)
            {
                try { store.Heartbeat(runId, result.PlannedItems); } catch { /* best-effort final total */ }
                // Persist new id mappings so the next copy of this list can update in place.
                if (result.ItemMappings.Count > 0)
                    store.SaveItemMap(pairKey, result.ItemMappings);
                store.FinishRun(runId, result, status);
            }
        }
        return (result, status);
    }
}
