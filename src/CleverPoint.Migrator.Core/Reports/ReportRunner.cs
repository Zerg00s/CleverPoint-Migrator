using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// Thrown by whoever supplies connections to say why a site cannot be scanned in a way the run
/// should record as its own status (signIn, noConnection) rather than as a plain failure.
/// </summary>
public sealed class ReportSiteException : Exception
{
    public string Status { get; }
    public ReportSiteException(string status, string message) : base(message) => Status = status;
}

/// <summary>
/// Runs one report over its sites. Used by the command line (in a detached process) and by the
/// desktop app (in the app), so a run behaves the same wherever it was started.
///
/// Sites are scanned several at a time. One site failing never stops the run: it is recorded with its
/// reason and the run carries on. Every finished site is saved as it finishes, so a run that is
/// cancelled, crashes or loses its sign-in can be continued and only does what is left.
/// </summary>
public static class ReportRunner
{
    public static string NewRunId() =>
        $"r{DateTime.UtcNow:yyMMdd}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";

    public static string LogPath(string runId) => Path.Combine(UxSettings.Folder, "logs", $"report-{runId}.log");

    /// <summary>
    /// Makes a report file independent of the folder it was in: site and pair files are read into the
    /// file itself, URLs are put in one form. The result is what gets stored with the run.
    /// </summary>
    public static (ReportSpec Spec, List<string> Notes) Materialize(ReportSpec spec, string? baseFolder)
    {
        var notes = new List<string>();
        if (spec.SitesFile is { } sitesFile)
        {
            var (sites, skipped) = ReportSpecs.ReadSitesFile(ReportSpecs.ResolveFile(sitesFile, baseFolder));
            spec.Sites = sites;
            spec.SitesFile = null;
            if (skipped > 0) notes.Add($"{skipped} line(s) of the sites file are not site URLs and were skipped.");
        }
        if (spec.PairsFile is { } pairsFile)
        {
            var (pairs, skipped) = ReportSpecs.ReadPairsFile(ReportSpecs.ResolveFile(pairsFile, baseFolder));
            spec.Pairs = pairs;
            spec.PairsFile = null;
            if (skipped > 0) notes.Add($"{skipped} line(s) of the pairs file do not hold two site URLs and were skipped.");
        }
        var before = spec.Sites.Count;
        spec.Sites = ReportSpecs.Distinct(spec.Sites);
        if (spec.Sites.Count < before) notes.Add($"{before - spec.Sites.Count} site(s) were listed more than once and are scanned once.");
        foreach (var pair in spec.Pairs)
        {
            pair.Source = JobSpecs.NormalizeSite(pair.Source);
            pair.Target = JobSpecs.NormalizeSite(pair.Target);
        }
        if (spec.Tenant is not null) spec.Tenant = JobSpecs.NormalizeSite(spec.Tenant);
        if (spec.AllSites is not null) spec.AllSites.Tenant = JobSpecs.NormalizeSite(spec.AllSites.Tenant);
        return (spec, notes);
    }

    /// <summary>
    /// Runs a queued report to the end and records how it ended. Returns the final status.
    /// </summary>
    /// <param name="connect">Gives a connection to a site without ever prompting.</param>
    public static async Task<string> ExecuteAsync(string runId, Func<string, Task<SpConnection>> connect, Action<string> log, CancellationToken stop = default)
    {
        ReportRun? run;
        using (var history = new HistoryStore(UxSettings.HistoryDbPath))
        {
            run = history.GetReportRun(runId);
            if (run is null || !history.StartReportRun(runId))
            {
                log(run is null ? $"report run {runId} not found" : $"report run {runId} is {run.Status}; nothing to do");
                return run?.Status ?? "NotFound";
            }
        }

        var store = new ReportStore(run.Folder);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var progress = new Progress();

        void Beat()
        {
            try
            {
                using var history = new HistoryStore(UxSettings.HistoryDbPath);
                if (history.ReportHeartbeat(runId, progress.Total, progress.Done, progress.Failed, progress.Rows, progress.Describe())) cancel.Cancel();
            }
            catch { /* a busy database must not stop the run */ }
        }
        using var heartbeat = new Timer(_ => Beat(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

        var status = "Completed";
        string? error = null;
        try
        {
            var issues = new List<ReportIssue>();
            var spec = ReportSpecs.Parse(run.SpecJson, issues) ?? throw new InvalidOperationException(issues[0].Message);
            var report = ReportCatalog.Find(spec.Report) ?? throw new InvalidOperationException($"There is no report '{spec.Report}'.");
            store.Repair();

            var sites = new SiteConnector(connect);
            var targets = await ResolveTargetsAsync(spec, report, store, sites.ConnectAsync, log, cancel.Token);
            if (targets.Count == 0) throw new InvalidOperationException("There are no sites to scan.");

            var done = store.Sites().Where(s => s.Ok).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = targets.Select((target, index) => (target, index)).Where(t => !done.Contains(t.target.Key)).ToList();
            progress.Total = targets.Count;
            progress.Done = targets.Count - pending.Count;
            progress.Rows = store.TotalRows();
            log($"report {report.Id}: {targets.Count} site(s), {pending.Count} to do, {Math.Clamp(spec.Parallel, 1, ReportSpecs.MaxParallel)} at a time" + (spec.Apply ? ", APPLY" : ""));
            Beat();

            var options = new ReportOptions(report, spec.Options);
            var closedSites = ClosedSites(store);

            await Parallel.ForEachAsync(pending,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(spec.Parallel, 1, ReportSpecs.MaxParallel), CancellationToken = cancel.Token },
                async (item, ct) =>
                {
                    var (target, index) = item;
                    var watch = Stopwatch.StartNew();
                    var writer = store.BeginSite(index);
                    var record = new SiteRecord { Key = target.Key, Site = target.Url, Target = target.TargetUrl };
                    progress.Enter(target.Url);
                    Beat();     // so "what is it doing now" is never blank between two sites
                    try
                    {
                        if (closedSites.TryGetValue(target.Url, out var why)) throw new ReportSiteException(why[0], why[1]);
                        var connection = await sites.ConnectAsync(target.Url);
                        var targetConnection = target.TargetUrl is null ? null : await sites.ConnectAsync(target.TargetUrl);
                        var context = new ReportContext
                        {
                            Site = target,
                            Connection = connection,
                            Target = targetConnection,
                            Options = options,
                            Apply = spec.Apply,
                            Cancel = ct,
                            Connect = sites.ConnectAsync,
                            PhaseSink = text => progress.Phase(target.Url, text),
                            RowSink = (table, row) =>
                            {
                                writer.Write(table, WithSite(report, target, row));
                                progress.AddRow();
                            },
                        };
                        await report.RunAsync(context);
                        ct.ThrowIfCancellationRequested();
                        record.Counts = context.Counters.Count > 0 ? new Dictionary<string, long>(context.Counters) : null;
                        record.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
                        record.FinishedUtc = DateTime.UtcNow;
                        store.Commit(writer, record);
                        progress.Leave(target.Url, failed: false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Not a result: the site was cut short and is simply done again on resume.
                        progress.Abandon(target.Url, writer.Rows);
                        writer.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        (record.Status, record.Error) = Classify(ex);
                        record.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
                        record.FinishedUtc = DateTime.UtcNow;
                        progress.Abandon(target.Url, writer.Rows);
                        store.Discard(writer, record);
                        progress.Leave(target.Url, failed: true);
                        log($"{record.Status} {target.Key}: {record.Error}");
                    }
                    Beat();
                });

            var records = store.Sites();
            var ok = records.Count(s => s.Ok);
            if (ok == 0)
            {
                status = "Failed";
                error = records.FirstOrDefault()?.Error ?? "No site could be scanned.";
                if (records.Count > 1) error = $"None of the {records.Count} sites could be scanned. First reason: {error}";
            }
            else if (ok < targets.Count) status = "CompletedWithIssues";
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
        }
        catch (Exception ex)
        {
            status = "Failed";
            (_, error) = Classify(ex);
            log(ex.ToString());
        }
        finally
        {
            heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
            try
            {
                // The numbers of record come from what is on disk, not from the counters kept while running.
                var sites = store.Sites();
                using var history = new HistoryStore(UxSettings.HistoryDbPath);
                history.ReportHeartbeat(runId, Math.Max(progress.Total, sites.Count), sites.Count(s => s.Ok), sites.Count(s => !s.Ok), store.TotalRows(), null);
                history.FinishReportRun(runId, status, error);
            }
            catch (Exception ex) { log("could not record the end of the run: " + ex.Message); }
            log($"report run {runId} finished: {status}" + (error is null ? "" : $" ({error})"));
        }
        return status;
    }

    /// <summary>
    /// Connections for the sites of a run. A saved connection covers a whole host, so it is looked up
    /// once per host and reused for every site on it; the look-up itself is not safe to run from
    /// several sites at once. A sign-in that is missing for a host is missing for every site on it, so
    /// that answer is remembered too instead of being asked for again per site.
    /// </summary>
    private sealed class SiteConnector
    {
        private readonly Func<string, Task<SpConnection>> _connect;
        private readonly ConcurrentDictionary<string, SpConnection> _byHost = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ReportSiteException> _blocked = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate = new(1, 1);

        public SiteConnector(Func<string, Task<SpConnection>> connect) => _connect = connect;

        public async Task<SpConnection> ConnectAsync(string url)
        {
            url = url.TrimEnd('/');
            var host = new Uri(url).Host;
            if (_blocked.TryGetValue(host, out var known)) throw new ReportSiteException(known.Status, known.Message);
            if (_byHost.TryGetValue(host, out var cached)) return cached.ForWeb(url);
            await _gate.WaitAsync();
            try
            {
                if (_blocked.TryGetValue(host, out known)) throw new ReportSiteException(known.Status, known.Message);
                if (_byHost.TryGetValue(host, out cached)) return cached.ForWeb(url);
                var connection = await _connect(url);
                _byHost[host] = connection;
                return connection;
            }
            catch (SignInRequiredException ex)
            {
                throw _blocked.GetOrAdd(host, new ReportSiteException("signIn", ex.Message));
            }
            catch (ReportSiteException ex) when (ex.Status is "signIn" or "noConnection")
            {
                throw _blocked.GetOrAdd(host, ex);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>Rows carry the site they came from, so tables of many sites can be filtered and exported per site.</summary>
    private static Dictionary<string, object?> WithSite(ReportDefinition report, SiteTarget target, Dictionary<string, object?> row)
    {
        if (report.Scope == ReportScope.Tenant) return row;
        var withSite = new Dictionary<string, object?>(row.Count + 2) { ["site"] = target.Url };
        if (target.TargetUrl is not null) withSite["targetSite"] = target.TargetUrl;
        foreach (var (name, value) in row) withSite[name] = value;
        return withSite;
    }

    /// <summary>Turns what the network and SharePoint throw into a status and one readable line.</summary>
    public static (string Status, string Error) Classify(Exception ex)
    {
        switch (ex)
        {
            case ReportSiteException site:
                return (site.Status, site.Message);
            case SignInRequiredException:
                return ("signIn", ex.Message);
            case SpRestException rest:
                var detail = RestMessage(rest);
                return rest.StatusCode switch
                {
                    401 or 403 => ("denied", detail == "."
                        ? $"Access denied (HTTP {rest.StatusCode}). The account or app has no rights on this site, or the site is locked or archived."
                        : $"Access denied (HTTP {rest.StatusCode}){detail}"),
                    // Only a 404 for the site itself means the site is missing. For anything inside it,
                    // the thing was most likely deleted while the report was reading.
                    404 => IsWebItself(rest.Url)
                        ? ("notFound", "There is no site at this address (HTTP 404).")
                        : ("failed", "Something the report was reading no longer exists (HTTP 404). It may have been deleted while the report ran."),
                    423 => ("denied", "The site is locked (HTTP 423)."),
                    _ => ("failed", $"SharePoint answered HTTP {rest.StatusCode}{detail}"),
                };
            case Microsoft.SharePoint.Client.ServerUnauthorizedAccessException:
                return ("denied", "Access denied.");
            case Microsoft.SharePoint.Client.ServerException server:
                return server.ServerErrorTypeName?.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) == true
                    ? ("denied", "Access denied.")
                    : ("failed", server.Message);
            case System.Net.WebException { Response: System.Net.HttpWebResponse response }:
                return (int)response.StatusCode switch
                {
                    401 or 403 => ("denied", $"Access denied (HTTP {(int)response.StatusCode})."),
                    404 => ("notFound", "There is no site at this address (HTTP 404)."),
                    var code => ("failed", $"SharePoint answered HTTP {code}."),
                };
            case HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException:
                return ("failed", "Could not reach the server: " + ex.Message.Split('\n')[0]);
            default:
                return ("failed", ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>True for a call on the site as a whole (its web, its list of lists), not on one thing inside it.</summary>
    private static bool IsWebItself(string url)
    {
        var at = url.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return true;
        var call = url[(at + 6)..].ToLowerInvariant();
        var path = call.Split('?')[0].TrimEnd('/');
        return path is "web" or "web/lists" or "web/webs" or "web/roleassignments" or "web/siteusers" or "web/features" or "site/features"
            or "web/currentuser" or "contextinfo" or "site";
    }

    /// <summary>The server's own explanation from a REST error body, shortened. Empty when there is none.</summary>
    private static string RestMessage(SpRestException ex)
    {
        var marker = ex.Message.IndexOf(": {", StringComparison.Ordinal);
        if (marker < 0) return ".";
        try
        {
            using var body = JsonDocument.Parse(ex.Message[(marker + 2)..]);
            var root = body.RootElement;
            var message = root.TryGetProperty("odata.error", out var error) && error.TryGetProperty("message", out var m) && m.TryGetProperty("value", out var v)
                ? v.GetString()
                : root.TryGetProperty("error", out var e2) && e2.TryGetProperty("message", out var m2)
                    ? (m2.ValueKind == JsonValueKind.String ? m2.GetString() : m2.TryGetProperty("value", out var v2) ? v2.GetString() : null)
                    : root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) return ".";
            message = message.Split('\n')[0].Trim();
            return ": " + (message.Length > 200 ? message[..200] + "…" : message);
        }
        catch (JsonException)
        {
            return ".";
        }
    }

    // ---- which sites -------------------------------------------------------------------------------

    private static string TargetsPath(ReportStore store) => Path.Combine(store.Folder, "targets.json");
    private static string ClosedPath(ReportStore store) => Path.Combine(store.Folder, "closed.json");

    /// <summary>Sites the tenant listing already knows nobody can open, with the status and reason to record for them.</summary>
    private static Dictionary<string, string[]> ClosedSites(ReportStore store)
    {
        try
        {
            if (File.Exists(ClosedPath(store)))
                return new Dictionary<string, string[]>(JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(ClosedPath(store))) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { /* then they are simply tried like any other site */ }
        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sites of the run. Worked out once and saved, so that continuing a run scans the same sites
    /// even if the tenant gained or lost some in between.
    /// </summary>
    private static async Task<List<SiteTarget>> ResolveTargetsAsync(ReportSpec spec, ReportDefinition report, ReportStore store,
        Func<string, Task<SpConnection>> connect, Action<string> log, CancellationToken ct)
    {
        if (File.Exists(TargetsPath(store)))
        {
            var saved = JsonSerializer.Deserialize<List<SiteTarget>>(File.ReadAllText(TargetsPath(store)));
            if (saved is { Count: > 0 }) return saved;
        }

        List<SiteTarget> targets;
        if (report.Scope == ReportScope.Tenant)
        {
            targets = new List<SiteTarget> { new(ReportSpecs.AdminUrl(spec.Tenant!)) };
        }
        else if (report.Scope == ReportScope.SitePairs)
        {
            targets = spec.Pairs.Select(p => new SiteTarget(p.Source, p.Target)).ToList();
        }
        else if (spec.AllSites is { } all)
        {
            log($"listing the sites of {new Uri(all.Tenant).Host}");
            var admin = await connect(ReportSpecs.AdminUrl(all.Tenant));
            var (urls, closed) = await TenantSites.ListUrlsAsync(admin, all.Type, all.UrlContains, all.Template, _ => { }, ct);
            targets = urls.Select(u => new SiteTarget(u)).ToList();
            log($"{urls.Count} site(s) listed" + (closed.Count > 0 ? $", {closed.Count} of them locked or archived" : ""));
            if (closed.Count > 0)
            {
                Directory.CreateDirectory(store.Folder);
                File.WriteAllText(ClosedPath(store), JsonSerializer.Serialize(closed.ToDictionary(c => c.Key, c => new[] { c.Value.Status, c.Value.Reason })));
            }
        }
        else if (spec.FromReport is { } fromRun)
        {
            ReportRun? source;
            using (var history = new HistoryStore(UxSettings.HistoryDbPath)) source = history.GetReportRun(fromRun);
            if (source is null) throw new InvalidOperationException($"There is no report run {fromRun} to take the sites from.");
            var sourceStore = new ReportStore(source.Folder);
            var urls = new List<string>();
            foreach (var table in sourceStore.RowCounts().Keys)
                foreach (var row in sourceStore.ReadRows(table))
                    if (row.TryGetValue("url", out var url) && url.ValueKind == JsonValueKind.String && ReportSpecs.SiteProblem(url.GetString()!) is null)
                        urls.Add(url.GetString()!);
            if (urls.Count == 0) throw new InvalidOperationException($"Report run {fromRun} has no site URLs. Use a run of the sites or onedrives report.");
            targets = ReportSpecs.Distinct(urls).Select(u => new SiteTarget(u)).ToList();
        }
        else
        {
            targets = spec.Sites.Select(s => new SiteTarget(s)).ToList();
        }

        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(TargetsPath(store), JsonSerializer.Serialize(targets), new UTF8Encoding(false));
        return targets;
    }

    /// <summary>The sites a run covers, once it has worked them out. Empty before that.</summary>
    public static List<SiteTarget> SavedTargets(ReportStore store)
    {
        try
        {
            if (File.Exists(TargetsPath(store)))
                return JsonSerializer.Deserialize<List<SiteTarget>>(File.ReadAllText(TargetsPath(store))) ?? new();
        }
        catch (JsonException) { /* treated as not worked out yet */ }
        return new List<SiteTarget>();
    }

    /// <summary>Where the run stands, kept in memory and written to the database with each heartbeat.</summary>
    private sealed class Progress
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);
        private long _rows;
        public int Total;
        public int Done;
        public int Failed;
        public long Rows { get => Interlocked.Read(ref _rows); set => Interlocked.Exchange(ref _rows, value); }

        public void AddRow() => Interlocked.Increment(ref _rows);
        public void Enter(string site) { lock (_lock) _active[site] = ""; }
        public void Phase(string site, string text) { lock (_lock) if (_active.ContainsKey(site)) _active[site] = text; }
        /// <summary>Rows of a site that did not make it into the tables no longer count.</summary>
        public void Abandon(string site, long rows) => Interlocked.Add(ref _rows, -rows);

        public void Leave(string site, bool failed)
        {
            lock (_lock)
            {
                _active.Remove(site);
                if (failed) Failed++; else Done++;
            }
        }

        public string? Describe()
        {
            lock (_lock)
            {
                if (_active.Count == 0) return null;
                var first = _active.First();
                // A phase that only repeats the site's own address adds nothing.
                var text = first.Key + (first.Value.Length > 0 && !first.Value.Equals(first.Key, StringComparison.OrdinalIgnoreCase) ? $" ({first.Value})" : "");
                return _active.Count == 1 ? text : $"{text} and {_active.Count - 1} more";
            }
        }
    }
}
