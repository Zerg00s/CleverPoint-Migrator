using System.Collections.Concurrent;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Reports;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Runs reports started in the desktop app. The work is done by the same runner the command line
/// uses and is recorded in the same place, so a report looks the same wherever it was started and
/// the Reports page also shows runs started with cpm.
/// </summary>
public sealed class ReportHost
{
    private readonly UxSettings _settings;
    private readonly BrowserSignIn _browser;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public ReportHost(UxSettings settings, BrowserSignIn browser)
    {
        _settings = settings;
        _browser = browser;
    }

    /// <summary>Raised when a run started here begins or ends.</summary>
    public event Action? Changed;

    /// <summary>Reports running in this app right now.</summary>
    public int ActiveCount => _running.Count;

    public static List<ReportRun> Runs(int limit = 200)
    {
        try
        {
            using var history = new HistoryStore(UxSettings.HistoryDbPath);
            history.MarkOrphanedAsInterrupted(includeOwnProcess: false);
            return history.GetReportRuns(limit);
        }
        catch
        {
            return new List<ReportRun>();
        }
    }

    public static ReportRun? Run(string id)
    {
        try
        {
            using var history = new HistoryStore(UxSettings.HistoryDbPath);
            return history.GetReportRun(id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Signs in, with a browser window if it has to, to every host the report will touch. Done before
    /// the run starts: once it is running nothing may prompt.
    /// </summary>
    public async Task<(bool Ok, string Message)> EnsureSignedInAsync(IEnumerable<string> sites)
    {
        _settings.EnsureLoaded();
        foreach (var site in sites.GroupBy(s => new Uri(s).Host, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var host = new Uri(site).Host;
            var connection = UxConnectionResolver.Find(_settings, site);
            if (connection is null)
                return (false, $"No connection covers {host}. Add one on the Connections page first.");
            if (UxConnectionResolver.IsBrowser(connection) && !_browser.HasSession(site))
            {
                var (ok, message) = await _browser.SignInAsync(site);
                if (!ok) return (false, $"Sign-in to {host} did not complete: {message}");
            }
            else if (UxConnectionResolver.IsDelegated(connection))
            {
                try
                {
                    if (await UxConnectionResolver.EnsureDelegatedAsync(connection, site)) _settings.Save();
                }
                catch (Exception ex)
                {
                    return (false, $"Sign-in to {host} did not complete: {ex.Message}");
                }
            }
        }
        return (true, "");
    }

    /// <summary>A connection for a site, without ever prompting. This is what the runner calls while a report runs.</summary>
    private async Task<SpConnection> ConnectAsync(string site)
    {
        var host = new Uri(site).Host;
        var connection = UxConnectionResolver.Find(_settings, site)
                         ?? throw new ReportSiteException("noConnection", $"No connection covers {host}. Add one on the Connections page.");
        if (UxConnectionResolver.IsDelegated(connection))
        {
            if (!UxConnectionResolver.IsSignedIn(connection))
                throw new ReportSiteException("signIn", $"Nobody is signed in to {host}. Connect it on the Connections page.");
            await UxConnectionResolver.DelegatedProvider(connection).GetTokenAsync(host);
        }
        else if (UxConnectionResolver.IsBrowser(connection) && !_browser.HasSession(site))
        {
            throw new ReportSiteException("signIn", $"The browser sign-in for {host} is missing or has expired. Connect it on the Connections page.");
        }
        return UxConnectionResolver.Resolve(connection, site, _browser);
    }

    /// <summary>A connection for a page that is about to list something itself, signing in first if it has to.</summary>
    public async Task<SpConnection> OpenAsync(string site)
    {
        var (ok, message) = await EnsureSignedInAsync(new[] { site });
        if (!ok) throw new InvalidOperationException(message);
        return await ConnectAsync(site);
    }

    /// <summary>Queues a report and starts it in the background. Returns its run ID.</summary>
    public string Start(ReportSpec spec, ReportDefinition report)
    {
        var id = ReportRunner.NewRunId();
        using (var history = new HistoryStore(UxSettings.HistoryDbPath))
            history.CreateReportRun(new ReportRun
            {
                Id = id,
                Report = report.Id,
                Name = string.IsNullOrWhiteSpace(spec.Name) ? report.Title : spec.Name!,
                SpecJson = JsonSerializer.Serialize(spec, ReportSpecs.Json),
                LogPath = ReportRunner.LogPath(id),
                Folder = ReportStore.FolderFor(id),
            });
        Execute(id);
        return id;
    }

    /// <summary>
    /// Starts the real run of an action from its finished preview: the same sites and settings, this
    /// time making the changes. Null when the run is not a preview that can be applied.
    /// </summary>
    public string? Apply(ReportRun preview)
    {
        var report = ReportCatalog.Find(preview.Report);
        var spec = ReportSpecs.Parse(preview.SpecJson, new List<ReportIssue>());
        if (report is not { ChangesData: true } || spec is null || spec.Apply || preview.Status is not ("Completed" or "CompletedWithIssues")) return null;
        spec.Apply = true;
        spec.Name = preview.Name + " (applied)";
        return Start(spec, report);
    }

    /// <summary>For an action: true when the run made changes, false when it was a preview. Null for a report that only reads.</summary>
    public static bool? WasApplied(ReportRun run) =>
        ReportCatalog.Find(run.Report) is { ChangesData: true } ? ReportSpecs.Parse(run.SpecJson, new List<ReportIssue>())?.Apply == true : null;

    /// <summary>Continues a run that stopped before every site was done. False when there is nothing to continue.</summary>
    public bool Resume(string id)
    {
        using (var history = new HistoryStore(UxSettings.HistoryDbPath))
        {
            history.MarkOrphanedAsInterrupted(includeOwnProcess: false);
            var run = history.GetReportRun(id);
            if (run is null || !run.CanResume || !history.RequeueReportRun(id)) return false;
        }
        Execute(id);
        return true;
    }

    private void Execute(string id)
    {
        var stop = new CancellationTokenSource();
        _running[id] = stop;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportRunner.LogPath(id))!);
                await using var log = new StreamWriter(ReportRunner.LogPath(id), append: true) { AutoFlush = true };
                await ReportRunner.ExecuteAsync(id, ConnectAsync, line =>
                {
                    lock (log) log.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");
                }, stop.Token);
            }
            catch (Exception ex)
            {
                AppLog.Error("Report " + id, ex);
            }
            finally
            {
                _running.TryRemove(id, out _);
                stop.Dispose();
                Changed?.Invoke();
            }
        });
    }

    /// <summary>Asks a report to stop, wherever it runs. Sites already scanned are kept.</summary>
    public void Cancel(string id)
    {
        try
        {
            using var history = new HistoryStore(UxSettings.HistoryDbPath);
            history.RequestCancelReportRun(id);
        }
        catch { /* the token below still stops a run of this app */ }
        if (_running.TryGetValue(id, out var stop))
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { /* it just finished */ }
        }
    }

    /// <summary>Stops every report running in this app, for when the app is closing.</summary>
    public void CancelAll()
    {
        foreach (var id in _running.Keys.ToList()) Cancel(id);
    }

    public static bool Delete(string id)
    {
        using var history = new HistoryStore(UxSettings.HistoryDbPath);
        var run = history.GetReportRun(id);
        if (run is null || !history.DeleteReportRun(id)) return false;
        try { new ReportStore(run.Folder).Delete(); } catch { /* files left behind do no harm */ }
        return true;
    }
}
