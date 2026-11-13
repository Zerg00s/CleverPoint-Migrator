using System.Text;
using System.Text.Json;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Reports;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>
/// Reports: what is in a tenant or a set of sites, who has access, what is customized. A report runs
/// over as many sites as it is given, in the background, and can be followed, cancelled and continued.
/// </summary>
public static class ReportCommands
{
    public static IEnumerable<Command> All() => new[]
    {
        new Command
        {
            Path = new[] { "report", "list" },
            Summary = "List the reports that can be run. With --report, show one report's settings and columns.",
            Options = new[] { new Opt("report", "<id>", "Show this report in full: its settings and the columns of its tables.") },
            Run = Catalog,
        },
        new Command
        {
            Path = new[] { "schema", "report" },
            Summary = "Print the JSON Schema of the report file.",
            Options = new[] { new Opt("raw", null, "Print the schema alone, without the ok/data wrapper, for saving to a file.") },
            Run = args => Task.FromResult(Raw(args, SchemaGenerator.For(typeof(ReportSpec), "CleverPoint Migrator report"))),
        },
        new Command
        {
            Path = new[] { "report", "template" },
            Summary = "Print a report file to start from, with the report's settings at their defaults.",
            Args = new[] { "<report>" },
            Options = new[] { new Opt("raw", null, "Print the file alone, without the ok/data wrapper, for saving to a file.") },
            Examples = new[] { "cpm report template lists --raw > lists.json" },
            Run = args => Task.FromResult(Raw(args, JsonDocument.Parse(ReportSpecs.Template(Definition(args.Arg(0, "<report>")))).RootElement)),
        },
        new Command
        {
            Path = new[] { "report", "start" },
            Summary = "Start a report in the background and return its run ID at once.",
            Args = new[] { "[file]" },
            Options = StartOptions().Concat(new[]
            {
                new Opt("wait", null, "Block until the report finishes (or --timeout passes) and return its final status."),
                new Opt("timeout", "<seconds>", "With --wait: give up waiting after this long. The report keeps running. Default 3600."),
            }).ToArray(),
            Notes = "Give a report file, or name the report and its sites with options. The report runs in its own process and survives this shell.\n" +
                    "Follow it with `cpm report status <runId>`, read results with `cpm report rows <runId>`, save them with `cpm report export <runId>`.\n" +
                    "A report that changes sites (an action) only lists what it would change unless --apply is given.",
            Examples = new[]
            {
                "cpm report start --report lists --sites-file sites.txt",
                "cpm report start --report sites --tenant https://contoso.sharepoint.com --wait",
                "cpm report start --report guests --all-sites https://contoso.sharepoint.com --parallel 6",
                "cpm report start report.json --wait --timeout 1800",
            },
            Run = StartAsync,
        },
        new Command
        {
            Path = new[] { "report", "validate" },
            Summary = "Check a report file. With --online, also check that every site can be opened.",
            Args = new[] { "[file]" },
            Options = StartOptions().Append(new Opt("online", null, "Also sign in silently and try to open every site. Changes nothing.")).ToArray(),
            Notes = "Takes a report file, or the same options as `cpm report start` instead of a file.",
            Run = ValidateAsync,
        },
        new Command
        {
            Path = new[] { "report", "status" },
            Summary = "Show where a report run stands: sites done and failed, rows so far, and its summary numbers.",
            Args = new[] { "<runId>" },
            Run = args => Task.FromResult(StatusOutcome(args.Arg(0, "<runId>"))),
        },
        new Command
        {
            Path = new[] { "report", "wait" },
            Summary = "Block until a report run finishes, then show its final status.",
            Args = new[] { "<runId>" },
            Options = new[] { new Opt("timeout", "<seconds>", "Give up waiting after this long (exit code 6). The report keeps running. Default 3600.") },
            Run = args => WaitAsync(args.Arg(0, "<runId>"), args.Int("timeout", 3600)),
        },
        new Command
        {
            Path = new[] { "report", "cancel" },
            Summary = "Ask a running report to stop. Sites already scanned are kept; it can be continued with resume.",
            Args = new[] { "<runId>" },
            Run = Cancel,
        },
        new Command
        {
            Path = new[] { "report", "resume" },
            Summary = "Continue a report that was cancelled, interrupted or had failed sites. Only what is not done yet is scanned.",
            Args = new[] { "<runId>" },
            Options = new[]
            {
                new Opt("wait", null, "Block until the report finishes (or --timeout passes)."),
                new Opt("timeout", "<seconds>", "With --wait: give up waiting after this long. Default 3600."),
            },
            Run = ResumeAsync,
        },
        new Command
        {
            Path = new[] { "report", "apply" },
            Summary = "Make the changes that a finished preview of an action listed. Starts a new run of the same action on the same sites.",
            Args = new[] { "<previewRunId>" },
            Options = new[]
            {
                new Opt("wait", null, "Block until the run finishes (or --timeout passes)."),
                new Opt("timeout", "<seconds>", "With --wait: give up waiting after this long. Default 3600."),
            },
            Notes = "Only for actions (reports marked as changing sites). Run the action without --apply first, show the user what it would change, and apply only after they agree.",
            Run = ApplyAsync,
        },
        new Command
        {
            Path = new[] { "report", "runs" },
            Summary = "List recent report runs.",
            Options = new[] { new Opt("limit", "<n>", "Most runs to return. Default 20.") },
            Run = Runs,
        },
        new Command
        {
            Path = new[] { "report", "sites" },
            Summary = "Show how each site of a report run ended, with the reason for the ones that failed.",
            Args = new[] { "<runId>" },
            Options = new[]
            {
                new Opt("failed", null, "Only the sites that could not be scanned."),
                new Opt("limit", "<n>", "Most sites to return. Default 200."),
                new Opt("offset", "<n>", "Skip this many first."),
            },
            Run = Sites,
        },
        new Command
        {
            Path = new[] { "report", "rows" },
            Summary = "Read the rows of a report run, a page at a time.",
            Args = new[] { "<runId>" },
            Options = new[]
            {
                new Opt("table", "<name>", "Which table. Needed only when the report has more than one."),
                new Opt("site", "<url>", "Only rows of this site."),
                new Opt("where", "<filters>", "Only rows that match, for example \"formType=PowerApps\" or \"listTitle~invoice,hidden=true\". = is equal, ~ is contains; case does not matter."),
                new Opt("sort", "<column>", "Sort by this column first: smallest or A first, or with --desc largest or Z first. With --limit this gives \"the top N\"."),
                new Opt("desc", null, "With --sort: largest first."),
                new Opt("columns", "<names>", "Only these columns, comma-separated."),
                new Opt("limit", "<n>", "Most rows to return. Default 50, at most 1000."),
                new Opt("offset", "<n>", "Skip this many matching rows first."),
                new Opt("count", null, "Return only how many rows match."),
            },
            Notes = "Rows can be read while the report is still running: the sites that have finished are there.",
            Examples = new[]
            {
                "cpm report rows r250101-ab12cd --where \"itemCount~0\" --columns site,listTitle,itemCount --limit 100",
                "cpm report rows r250101-ab12cd --table folders --sort totalBytes --desc --limit 20",
            },
            Run = Rows,
        },
        new Command
        {
            Path = new[] { "report", "export" },
            Summary = "Save a report run as an Excel workbook or as CSV files.",
            Args = new[] { "<runId>" },
            Options = new[]
            {
                new Opt("format", "<type>", "xlsx (default): one workbook with a summary, the sites and a sheet per table. csv: one file per table."),
                new Opt("out", "<file>", "Where to save. Default: the Downloads folder."),
            },
            Run = Export,
        },
        new Command
        {
            Path = new[] { "report", "delete" },
            Summary = "Delete a finished report run and its files.",
            Args = new[] { "<runId>" },
            Run = Delete,
        },
        new Command
        {
            Path = new[] { "report", "exec" },
            Summary = "Execute a queued report run in this process.",
            Args = new[] { "<runId>" },
            Options = new[] { new Opt("foreground", null, "Keep logging to this console instead of the run's log file.") },
            Hidden = true,
            Run = async args =>
            {
                var runId = args.Arg(0, "<runId>");
                var detached = !args.Has("foreground");
                await ExecuteAsync(runId, detached);
                if (detached) Environment.Exit(Exit.Ok);
                return StatusOutcome(runId);
            },
        },
    };

    private static IEnumerable<Opt> StartOptions() => new[]
    {
        new Opt("report", "<id>", "Which report, when no file is given. See: cpm report list"),
        new Opt("sites", "<urls>", "Site URLs to scan, comma-separated."),
        new Opt("sites-file", "<file>", "A text or CSV file with one site URL per line."),
        new Opt("all-sites", "<tenant url>", "Scan every site of this tenant. Needs a connection that can reach its admin site."),
        new Opt("type", "<kind>", "With --all-sites: sharepoint (default), onedrive or all."),
        new Opt("url-contains", "<text>", "With --all-sites: only sites whose URL contains this."),
        new Opt("from-report", "<runId>", "Take the sites from an earlier run of the sites or onedrives report."),
        new Opt("pairs-file", "<file>", "For compare reports: a CSV file with source URL and target URL per line."),
        new Opt("source", "<url>", "For compare reports: the source site of a single pair. Goes with --target."),
        new Opt("target", "<url>", "For compare reports: the target site of a single pair."),
        new Opt("tenant", "<url>", "For tenant-wide reports: any site of the tenant."),
        new Opt("set", "<settings>", "The report's own settings, for example \"includeHidden=true,includeSubsites=true\". A setting that takes a list: \"extensions=pdf;docx\"."),
        new Opt("parallel", "<n>", "Sites scanned at the same time, 1 to 8. Default 4."),
        new Opt("name", "<text>", "Name shown in the list of runs."),
        new Opt("apply", null, "Actions only: make the changes. Without it an action only lists what it would change."),
    };

    private static Outcome Raw(Invocation args, JsonElement document)
    {
        if (!args.Has("raw")) return new Outcome(document);
        Console.Out.WriteLine(JsonSerializer.Serialize(document, new JsonSerializerOptions(Runner.Json) { WriteIndented = true }));
        Environment.Exit(Exit.Ok);
        return new Outcome(null);
    }

    private static ReportDefinition Definition(string id) =>
        ReportCatalog.Find(id) ?? throw CliError.NotFound($"There is no report '{id}'.",
            "Reports: " + string.Join(", ", ReportCatalog.All.Select(r => r.Id)));

    // ---- catalog -----------------------------------------------------------------------------------

    private static Task<Outcome> Catalog(Invocation args)
    {
        if (args.Get("report") is { } id)
        {
            var report = Definition(id);
            return Task.FromResult(new Outcome(new
            {
                id = report.Id,
                title = report.Title,
                group = report.Group,
                summary = report.Summary,
                scope = ReportCatalog.ScopeName(report.Scope),
                needsTenantAdmin = report.NeedsTenantAdmin,
                changesData = report.ChangesData,
                meantFor = report.DefaultSiteType is { } kind ? $"{kind} sites: use --all-sites <tenant> --type {kind}" : null,
                settings = report.Options.Select(o => new { name = o.Name, type = o.Type, @default = o.Default, choices = o.Choices, help = o.Help }).ToList(),
                tables = report.Tables.Select(t => new
                {
                    name = t.Name,
                    columns = ReportCatalog.Columns(report, t).Select(c => new { name = c.Name, type = c.Type.ToString().ToLowerInvariant() }).ToList(),
                }).ToList(),
                start = StartExample(report),
            }));
        }
        return Task.FromResult(new Outcome(new
        {
            reports = ReportCatalog.All.Select(r => new
            {
                id = r.Id,
                group = r.Group,
                summary = r.Summary,
                scope = ReportCatalog.ScopeName(r.Scope),
                needsTenantAdmin = r.NeedsTenantAdmin ? true : (bool?)null,
                changesData = r.ChangesData ? true : (bool?)null,
            }).ToList(),
            scopes = new
            {
                sites = "Runs per site. Give the sites with --sites, --sites-file, --all-sites or --from-report.",
                tenant = "Covers a whole tenant through its admin site. Give any site of the tenant with --tenant.",
                sitePairs = "Compares source and target sites. Give one pair with --source and --target, or many with --pairs-file.",
            },
            next = "cpm report list --report <id>   (settings and columns)   then   cpm report start --report <id> ...",
        }));
    }

    private static string StartExample(ReportDefinition report) => report.Scope switch
    {
        ReportScope.Tenant => $"cpm report start --report {report.Id} --tenant https://contoso.sharepoint.com",
        ReportScope.SitePairs => $"cpm report start --report {report.Id} --source <url> --target <url>   or   --pairs-file pairs.csv",
        _ => $"cpm report start --report {report.Id} --sites-file sites.txt",
    };

    // ---- the report file, from a file or from options ----------------------------------------------

    /// <summary>The report to run, complete and checked: sites read in, URLs in one form.</summary>
    private static (ReportSpec Spec, ReportDefinition Report, List<string> Notes) Load(Invocation args)
    {
        ReportSpec spec;
        string? baseFolder;
        if (args.Positionals.Count > 0)
        {
            var file = args.Arg(0, "[file]");
            if (!File.Exists(file)) throw CliError.NotFound($"Report file not found: {file}", "On WSL, pass a Windows path: wslpath -w ./report.json");
            var issues = new List<ReportIssue>();
            spec = ReportSpecs.Parse(File.ReadAllText(file), issues)
                   ?? throw CliError.Invalid("The report file is not valid.", "See the format with: cpm schema report", issues);
            baseFolder = Path.GetDirectoryName(Path.GetFullPath(file));
            // Options given next to a file override what the file says.
            Overlay(spec, args);
        }
        else
        {
            if (!args.Has("report"))
                throw CliError.Usage("Give a report file, or name the report with --report.", "See the reports with: cpm report list");
            spec = new ReportSpec { Report = args.Get("report")! };
            baseFolder = Directory.GetCurrentDirectory();
            Overlay(spec, args);
        }

        var problems = ReportSpecs.Validate(spec, baseFolder);
        if (problems.Count > 0)
            throw CliError.Invalid($"The report cannot be run as given: {problems[0].Message}",
                ReportCatalog.Find(spec.Report) is { } known ? $"See its settings with: cpm report list --report {known.Id}" : "See the reports with: cpm report list",
                problems);

        var report = ReportCatalog.Find(spec.Report)!;
        var (complete, notes) = ReportRunner.Materialize(spec, baseFolder);
        if (report.Scope == ReportScope.Sites && complete.AllSites is null && complete.FromReport is null && complete.Sites.Count == 0)
            throw CliError.Invalid("The sites file holds no site URLs.", "Put one full https site URL per line.");
        if (report.Scope == ReportScope.SitePairs && complete.Pairs.Count == 0)
            throw CliError.Invalid("The pairs file holds no pairs.", "Put a source URL and a target URL per line, separated by a comma.");
        return (complete, report, notes);
    }

    private static void Overlay(ReportSpec spec, Invocation args)
    {
        if (args.Get("report") is { } report) spec.Report = report;
        if (args.Get("name") is { } name) spec.Name = name;
        if (args.Get("sites") is { } sites)
            spec.Sites = sites.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (args.Get("sites-file") is { } sitesFile) spec.SitesFile = Path.GetFullPath(sitesFile);
        if (args.Get("pairs-file") is { } pairsFile) spec.PairsFile = Path.GetFullPath(pairsFile);
        if (args.Get("from-report") is { } fromReport) spec.FromReport = fromReport;
        if (args.Has("source") != args.Has("target")) throw CliError.Usage("--source and --target go together: one pair of sites.");
        if (args.Get("source") is { } pairSource) spec.Pairs.Add(new ReportPair { Source = pairSource, Target = args.Get("target")! });
        if (args.Get("tenant") is { } tenant) spec.Tenant = tenant;
        if (args.Get("all-sites") is { } allSites)
            spec.AllSites = new ReportAllSites { Tenant = allSites, Type = args.Get("type") ?? "sharepoint", UrlContains = args.Get("url-contains") };
        else if (args.Has("type") || args.Has("url-contains"))
            throw CliError.Usage("--type and --url-contains go with --all-sites.");
        if (args.Has("parallel")) spec.Parallel = args.Int("parallel", 4);
        if (args.Has("apply")) spec.Apply = true;
        if (args.Get("set") is { } settings)
        {
            foreach (var pair in settings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) throw CliError.Usage($"--set needs name=value pairs, got '{pair}'.", "Example: --set includeHidden=true,includeSubsites=true");
                var setting = pair[..equals].Trim();
                var value = pair[(equals + 1)..].Trim();
                // A setting that takes a list gets its values separated by semicolons. Otherwise true,
                // false and whole numbers are taken as such, and anything else is text.
                var isList = ReportCatalog.Find(spec.Report)?.Options.FirstOrDefault(o => o.Name.Equals(setting, StringComparison.OrdinalIgnoreCase))?.Type == "list";
                var json = isList ? JsonSerializer.Serialize(value.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    : value.ToLowerInvariant() is "true" or "false" ? value.ToLowerInvariant()
                    : int.TryParse(value, out var number) ? number.ToString()
                    : JsonSerializer.Serialize(value);
                spec.Options[setting] = JsonDocument.Parse(json).RootElement.Clone();
            }
        }
    }

    /// <summary>The sites named in the file (not the ones a tenant listing would produce).</summary>
    private static List<string> NamedSites(ReportSpec spec, ReportDefinition report) => report.Scope switch
    {
        ReportScope.Tenant => new List<string> { ReportSpecs.AdminUrl(spec.Tenant!) },
        ReportScope.SitePairs => spec.Pairs.SelectMany(p => new[] { p.Source, p.Target }).ToList(),
        _ => spec.AllSites is { } all ? new List<string> { ReportSpecs.AdminUrl(all.Tenant) } : spec.Sites,
    };

    /// <summary>
    /// Fails now, with the command that fixes it, when a host of the report has no usable connection.
    /// One check per host: a connection covers every site on its host.
    /// </summary>
    private static async Task<List<object>> CheckConnectionsAsync(Session session, ReportSpec spec, ReportDefinition report)
    {
        var hosts = new List<object>();
        foreach (var site in NamedSites(spec, report).GroupBy(s => new Uri(s).Host, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var connection = session.Connection(site);
            await session.ConnectAsync(site);
            var admin = new Uri(site).Host.Contains("-admin.", StringComparison.OrdinalIgnoreCase);
            if (admin && UxConnectionResolver.IsBrowser(connection) && !new Uri(connection.SiteUrl).Host.Contains("-admin.", StringComparison.OrdinalIgnoreCase))
                throw CliError.Invalid($"'{report.Id}' needs the tenant's admin site, and the browser sign-in for {new Uri(connection.SiteUrl).Host} does not cover it.",
                    "Use an app + certificate or Microsoft sign-in connection for this tenant, or add a browser connection for the admin site itself.");
            hosts.Add(new { host = new Uri(site).Host, connection = connection.Name, signIn = Session.SignInKind(connection) });
        }
        return hosts;
    }

    private static Func<string, Task<SpConnection>> Connector(Session session) => async url =>
    {
        try
        {
            return await session.ConnectAsync(url);
        }
        catch (CliError ex) when (ex.Code == "sign_in_required")
        {
            throw new ReportSiteException("signIn", ex.Message + (ex.Hint is null ? "" : " " + ex.Hint));
        }
        catch (CliError ex) when (ex.Code == "not_found")
        {
            throw new ReportSiteException("noConnection", ex.Message);
        }
    };

    // ---- validate, start, exec ---------------------------------------------------------------------

    private static async Task<Outcome> ValidateAsync(Invocation args)
    {
        var (spec, report, notes) = Load(args);
        var sites = NamedSites(spec, report);
        if (!args.Has("online"))
            return new Outcome(new
            {
                valid = true,
                checkedOnline = false,
                report = report.Id,
                scope = ReportCatalog.ScopeName(report.Scope),
                sites = report.Scope == ReportScope.Sites && spec.AllSites is null && spec.FromReport is null ? spec.Sites.Count : (int?)null,
                pairs = report.Scope == ReportScope.SitePairs ? spec.Pairs.Count : (int?)null,
                allSitesOf = spec.AllSites?.Tenant,
                changes = report.ChangesData ? (spec.Apply ? "This run CHANGES sites." : "Preview only: nothing is changed without apply.") : null,
                notes = notes.Count > 0 ? notes : null,
                next = "Add --online to check that every site can be opened.",
            });

        var session = new Session();
        var hosts = await CheckConnectionsAsync(session, spec, report);
        // Open every named site once. This is the same probe the site-access report makes.
        var connect = Connector(session);
        var problems = new List<object>();
        var opened = 0;
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(sites.Distinct(StringComparer.OrdinalIgnoreCase).Select(async site =>
        {
            await gate.WaitAsync();
            try
            {
                var connection = await connect(site);
                using var _ = await connection.Rest.GetJsonAsync($"{connection.SiteUrl}/_api/web?$select=Title");
                Interlocked.Increment(ref opened);
            }
            catch (Exception ex)
            {
                var (status, error) = ReportRunner.Classify(ex);
                lock (problems) problems.Add(new { site, status, error });
            }
            finally { gate.Release(); }
        }));
        return new Outcome(new
        {
            valid = true,
            checkedOnline = true,
            report = report.Id,
            connections = hosts,
            sitesOpened = opened,
            sitesWithProblems = problems.Count,
            problems = problems.Count > 0 ? problems.Take(50).ToList() : null,
            notes = notes.Count > 0 ? notes : null,
            next = problems.Count == 0 ? "cpm report start ..." : "Sites with problems are skipped by the run and listed in: cpm report sites <runId> --failed",
        }, problems.Count > 0 ? Exit.CompletedWithIssues : Exit.Ok);
    }

    private static async Task<Outcome> StartAsync(Invocation args)
    {
        var (spec, report, notes) = Load(args);
        var session = new Session();
        // Fail now rather than in a background process nobody is watching.
        await CheckConnectionsAsync(session, spec, report);

        var id = ReportRunner.NewRunId();
        var name = !string.IsNullOrWhiteSpace(spec.Name) ? spec.Name!
            : args.Positionals.Count > 0 ? Path.GetFileNameWithoutExtension(args.Arg(0, "[file]"))
            : report.Title;
        using (var store = Session.OpenHistory())
            store.CreateReportRun(new ReportRun
            {
                Id = id, Report = report.Id, Name = name, SpecJson = JsonSerializer.Serialize(spec, ReportSpecs.Json),
                LogPath = ReportRunner.LogPath(id), Folder = ReportStore.FolderFor(id),
            });
        JobRunner.SpawnDetached("report", "exec", id);

        if (args.Has("wait")) return await WaitAsync(id, args.Int("timeout", 3600));
        return new Outcome(new
        {
            runId = id,
            report = report.Id,
            status = "Queued",
            changes = report.ChangesData ? (spec.Apply ? "This run CHANGES sites." : "Preview only: nothing is changed.") : null,
            notes = notes.Count > 0 ? notes : null,
            next = $"cpm report status {id}",
            orBlock = $"cpm report wait {id} --timeout 600",
        });
    }

    private static async Task<Outcome> ApplyAsync(Invocation args)
    {
        var previewId = args.Arg(0, "<previewRunId>");
        var (preview, report, _) = Open(previewId);
        if (report is null || !report.ChangesData)
            throw CliError.Invalid($"Report run {previewId} is a '{preview.Report}' report, which only reads. There is nothing to apply.");
        var spec = ReportSpecs.Parse(preview.SpecJson, new List<ReportIssue>())
                   ?? throw CliError.Invalid($"The settings of report run {previewId} can no longer be read.");
        if (spec.Apply) throw CliError.Invalid($"Report run {previewId} already made its changes; it was not a preview.");
        if (!preview.IsFinished)
            throw CliError.Invalid($"The preview {previewId} is still {preview.Status.ToLowerInvariant()}.", $"Wait for it with: cpm report wait {previewId}");
        if (preview.Status is not ("Completed" or "CompletedWithIssues"))
            throw CliError.Invalid($"The preview {previewId} ended as {preview.Status}, so what it would change is not known.",
                $"Run it to the end first: cpm report resume {previewId}");

        spec.Apply = true;
        spec.Name = preview.Name + " (applied)";
        var session = new Session();
        await CheckConnectionsAsync(session, spec, report);
        var id = ReportRunner.NewRunId();
        using (var store = Session.OpenHistory())
            store.CreateReportRun(new ReportRun
            {
                Id = id, Report = report.Id, Name = spec.Name, SpecJson = JsonSerializer.Serialize(spec, ReportSpecs.Json),
                LogPath = ReportRunner.LogPath(id), Folder = ReportStore.FolderFor(id),
            });
        JobRunner.SpawnDetached("report", "exec", id);
        if (args.Has("wait")) return await WaitAsync(id, args.Int("timeout", 3600));
        return new Outcome(new { runId = id, report = report.Id, appliesPreview = previewId, status = "Queued", changes = "This run CHANGES sites.", next = $"cpm report status {id}" });
    }

    private static async Task<Outcome> ResumeAsync(Invocation args)
    {
        var id = args.Arg(0, "<runId>");
        using (var store = Session.OpenHistory())
        {
            store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
            var run = store.GetReportRun(id) ?? throw CliError.NotFound($"No report run {id}.", "List runs with: cpm report runs");
            if (!run.IsFinished) throw CliError.Invalid($"Report run {id} is still {run.Status.ToLowerInvariant()}.", $"Follow it with: cpm report status {id}");
            if (!run.CanResume) throw CliError.Invalid($"Report run {id} finished with every site scanned; there is nothing to continue.",
                "Start it again as a new run with: cpm report start ...");
            store.RequeueReportRun(id);
        }
        JobRunner.SpawnDetached("report", "exec", id);
        if (args.Has("wait")) return await WaitAsync(id, args.Int("timeout", 3600));
        return new Outcome(new { runId = id, status = "Queued", message = "Continuing: sites already scanned are kept.", next = $"cpm report status {id}" });
    }

    private static async Task ExecuteAsync(string runId, bool detached)
    {
        if (detached)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportRunner.LogPath(runId))!);
            var log = new StreamWriter(ReportRunner.LogPath(runId), append: true, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }
        var session = new Session();
        await ReportRunner.ExecuteAsync(runId, Connector(session), line => Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}"));
    }

    // ---- following a run ---------------------------------------------------------------------------

    private static async Task<Outcome> WaitAsync(string runId, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (true)
        {
            // Whether it has finished is read first, the status after: the other way round a run that
            // finishes between the two would be reported as finished with the status of a moment before.
            bool finished;
            using (var store = Session.OpenHistory())
                finished = (store.GetReportRun(runId) ?? throw CliError.NotFound($"No report run {runId}.", "List runs with: cpm report runs")).IsFinished;
            var outcome = StatusOutcome(runId);
            if (finished) return outcome;
            if (DateTime.UtcNow >= deadline)
                return new Outcome(new { timedOut = true, run = outcome.Data, next = $"cpm report wait {runId} --timeout {timeoutSeconds}" }, Exit.Timeout);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static (ReportRun Run, ReportDefinition? Report, ReportStore Store) Open(string runId)
    {
        using var history = Session.OpenHistory();
        // A run whose process died would otherwise say "Running" forever.
        history.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        var run = history.GetReportRun(runId) ?? throw CliError.NotFound($"No report run {runId}.", "List runs with: cpm report runs");
        return (run, ReportCatalog.Find(run.Report), new ReportStore(run.Folder));
    }

    public static Outcome StatusOutcome(string runId)
    {
        var (run, report, store) = Open(runId);
        var counts = store.RowCounts();
        var sites = store.Sites();
        var failed = sites.Where(s => !s.Ok).ToList();
        var exit = run.Status switch
        {
            "Completed" or "Queued" or "Running" => Exit.Ok,
            "CompletedWithIssues" => Exit.CompletedWithIssues,
            "Cancelled" or "Interrupted" => Exit.Cancelled,
            _ => Exit.CompletedWithIssues,
        };
        var tenantWide = report?.Scope == ReportScope.Tenant;
        // An action says in every status whether it only looked or actually changed things.
        bool? applied = report is { ChangesData: true } ? ReportSpecs.Parse(run.SpecJson, new List<ReportIssue>())?.Apply == true : null;
        return new Outcome(new
        {
            runId = run.Id,
            report = run.Report,
            name = run.Name,
            status = run.Status,
            mode = applied switch { true => "applied: this run changed sites", false => "preview: nothing was changed", null => null },
            finished = run.IsFinished,
            error = run.Error,
            started = run.StartedUtc,
            finishedAt = run.FinishedUtc,
            elapsedSeconds = run.StartedUtc is { } started ? (int)((run.FinishedUtc ?? DateTime.UtcNow) - started).TotalSeconds : (int?)null,
            sitesTotal = tenantWide ? null : (int?)run.SitesTotal,
            sitesDone = tenantWide ? null : (int?)(run.IsFinished ? sites.Count(s => s.Ok) : run.SitesDone),
            sitesFailed = tenantWide ? null : (int?)(run.IsFinished ? failed.Count : run.SitesFailed),
            rows = run.IsFinished ? counts.Values.Sum() : run.Rows,
            tables = counts.Count > 0 ? counts : null,
            summary = store.Summary() is { Count: > 0 } summary ? summary : null,
            now = run.IsFinished ? null : run.Current,
            failures = failed.Count > 0
                ? failed.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count())
                : null,
            canResume = run.CanResume ? true : (bool?)null,
            log = run.LogPath,
            next = !run.IsFinished ? $"cpm report wait {run.Id} --timeout 600"
                : applied == false && run.Status is "Completed" or "CompletedWithIssues"
                    ? $"cpm report rows {run.Id}   (what would change; show the user)   then, only if they agree:   cpm report apply {run.Id}"
                : failed.Count > 0 ? $"cpm report sites {run.Id} --failed   (why)   then   cpm report resume {run.Id}   (try those again)"
                : run.CanResume ? $"cpm report resume {run.Id}"
                : counts.Values.Sum() > 0 ? $"cpm report rows {run.Id} --limit 50   or   cpm report export {run.Id}"
                : null,
        }, exit);
    }

    private static Task<Outcome> Cancel(Invocation args)
    {
        var id = args.Arg(0, "<runId>");
        using var store = Session.OpenHistory();
        var run = store.GetReportRun(id) ?? throw CliError.NotFound($"No report run {id}.", "List runs with: cpm report runs");
        var requested = store.RequestCancelReportRun(id);
        return Task.FromResult(new Outcome(new
        {
            runId = id,
            cancelRequested = requested,
            message = requested
                ? $"The report stops within a few seconds. Sites already scanned are kept. Continue later with: cpm report resume {id}"
                : $"The report is already {run.Status}.",
        }));
    }

    private static Task<Outcome> Runs(Invocation args)
    {
        using var store = Session.OpenHistory();
        store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        var runs = store.GetReportRuns(Math.Clamp(args.Int("limit", 20), 1, 500));
        return Task.FromResult(new Outcome(new
        {
            runs = runs.Select(r => new
            {
                runId = r.Id, report = r.Report, name = r.Name, status = r.Status, created = r.CreatedUtc, finished = r.FinishedUtc,
                sitesTotal = r.SitesTotal, sitesDone = r.SitesDone, sitesFailed = r.SitesFailed, rows = r.Rows, error = r.Error,
            }).ToList(),
        }));
    }

    private static Task<Outcome> Sites(Invocation args)
    {
        var (run, _, store) = Open(args.Arg(0, "<runId>"));
        var all = store.Sites();
        var filtered = args.Has("failed") ? all.Where(s => !s.Ok).ToList() : all;
        var limit = Math.Clamp(args.Int("limit", 200), 1, 5000);
        var offset = Math.Max(0, args.Int("offset", 0));
        // Sites the run has not reached yet have no record; say how many, so "done" is not mistaken for "all".
        var planned = ReportRunner.SavedTargets(store).Count;
        return Task.FromResult(new Outcome(new
        {
            runId = run.Id,
            status = run.Status,
            total = filtered.Count,
            notReachedYet = planned > all.Count ? planned - all.Count : (int?)null,
            truncated = filtered.Count > offset + limit ? true : (bool?)null,
            sites = filtered.Skip(offset).Take(limit).Select(s => new
            {
                site = s.Site, target = s.Target, status = s.Status, error = s.Error, rows = s.Rows, seconds = s.Seconds, counts = s.Counts,
            }).ToList(),
        }));
    }

    private static Task<Outcome> Rows(Invocation args)
    {
        var (run, report, store) = Open(args.Arg(0, "<runId>"));
        var counts = store.RowCounts();
        var tables = report?.Tables.Select(t => t.Name).ToList() ?? counts.Keys.ToList();
        var table = args.Get("table") ?? (tables.Count == 1 ? tables[0] : null)
            ?? throw CliError.Usage($"This report has several tables. Pick one with --table.", "Tables: " + string.Join(", ", tables));
        if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
            throw CliError.NotFound($"The report has no table '{table}'.", "Tables: " + string.Join(", ", tables));
        table = tables.First(t => t.Equals(table, StringComparison.OrdinalIgnoreCase));

        var known = report?.Tables.FirstOrDefault(t => t.Name == table) is { } definition
            ? ReportCatalog.Columns(report!, definition).Select(c => c.Name).ToList()
            : null;
        void CheckColumn(string column)
        {
            if (known is not null && !known.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw CliError.Usage($"The table '{table}' has no column '{column}'.", "Columns: " + string.Join(", ", known));
        }

        var filters = new List<RowFilter>();
        if (args.Get("site") is { } site) filters.Add(new RowFilter("site", "=", JobSpecs.NormalizeSite(site)));
        if (args.Get("where") is { } where)
        {
            // Filters are separated by commas, and a value may have a comma of its own ("Smith, John").
            // A piece starts a new filter only when it begins with a column of the table; otherwise it
            // is the rest of the value before it.
            var open = false;
            foreach (var piece in where.Split(','))
            {
                var part = piece.Trim();
                var at = part.IndexOfAny(new[] { '=', '~' });
                var column = at > 0 ? part[..at].Trim() : "";
                var starts = at > 0 && (known is null || column == "*" || known.Contains(column, StringComparer.OrdinalIgnoreCase));
                if (starts)
                {
                    filters.Add(new RowFilter(column, part[at].ToString(), part[(at + 1)..].Trim()));
                    open = true;
                }
                else if (open)
                {
                    var last = filters[^1];
                    filters[^1] = last with { Value = last.Value + "," + piece.TrimEnd() };
                }
                else if (part.Length > 0)
                {
                    if (at <= 0) throw CliError.Usage($"--where needs column=value or column~text, got '{part}'.");
                    CheckColumn(column);
                }
            }
        }
        var columns = args.Get("columns")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (columns is not null) foreach (var column in columns) CheckColumn(column);

        if (args.Has("count"))
            return Task.FromResult(new Outcome(new { runId = run.Id, table, matching = store.CountRows(table, filters), total = counts.GetValueOrDefault(table) }));

        var limit = Math.Clamp(args.Int("limit", 50), 1, 1000);
        var offset = Math.Max(0, args.Int("offset", 0));
        if (args.Has("desc") && !args.Has("sort")) throw CliError.Usage("--desc goes with --sort <column>.");
        List<Dictionary<string, JsonElement>> rows;
        bool more;
        long? matching = null;
        if (args.Get("sort") is { } sortColumn)
        {
            CheckColumn(sortColumn);
            if (offset + limit > 10_000) throw CliError.Usage("With --sort, --offset plus --limit can be at most 10000.", "Narrow the rows down with --where.");
            (rows, var matched) = store.ReadSorted(table, sortColumn, args.Has("desc"), offset, limit, filters);
            matching = matched;
            more = matched > offset + rows.Count;
        }
        else (rows, more) = store.ReadPage(table, offset, limit, filters);
        var shaped = columns is null
            ? rows
            : rows.Select(row => columns.Where(c => row.Keys.Any(k => k.Equals(c, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(c => c, c => row.First(p => p.Key.Equals(c, StringComparison.OrdinalIgnoreCase)).Value)).ToList();
        return Task.FromResult(new Outcome(new
        {
            runId = run.Id,
            table,
            status = run.IsFinished ? null : run.Status,
            total = filters.Count == 0 ? counts.GetValueOrDefault(table) : (long?)null,
            matching = filters.Count > 0 ? matching : null,
            offset,
            returned = shaped.Count,
            more = more ? true : (bool?)null,
            next = more ? $"--offset {offset + limit}" : null,
            note = "Empty values are left out of a row.",
            rows = shaped,
        }));
    }

    private static Task<Outcome> Export(Invocation args)
    {
        var (run, report, store) = Open(args.Arg(0, "<runId>"));
        if (report is null) throw CliError.Invalid($"This version of the tool does not know the report '{run.Report}'.");
        var format = (args.Get("format") ?? "xlsx").ToLowerInvariant();
        if (format is not ("xlsx" or "csv")) throw CliError.Usage($"--format '{format}' is not known.", "Use xlsx or csv.");
        var path = args.Get("out") is { } given
            ? Session.OutputFile(given)
            : LogExporter.DownloadsPath($"{report.Id}-{run.Id}", format);
        if (!path.EndsWith("." + format, StringComparison.OrdinalIgnoreCase)) path += "." + format;

        List<string> files;
        List<string> notes;
        if (format == "csv")
        {
            files = ReportExport.ExportCsv(run, report, store, path);
            notes = new List<string>();
        }
        else
        {
            notes = ReportExport.ExportXlsx(run, report, store, path);
            files = new List<string> { path };
        }
        if (!run.IsFinished) notes.Add("The report is still running: this export has the sites finished so far.");
        return Task.FromResult(new Outcome(new
        {
            runId = run.Id,
            format,
            files,
            rows = store.RowCounts(),
            notes = notes.Count > 0 ? notes : null,
        }));
    }

    private static Task<Outcome> Delete(Invocation args)
    {
        var id = args.Arg(0, "<runId>");
        var (run, _, store) = Open(id);
        if (!run.IsFinished) throw CliError.Invalid($"Report run {id} is still {run.Status.ToLowerInvariant()}.", $"Stop it first with: cpm report cancel {id}");
        using (var history = Session.OpenHistory()) history.DeleteReportRun(id);
        store.Delete();
        try { if (File.Exists(ReportRunner.LogPath(id))) File.Delete(ReportRunner.LogPath(id)); } catch { /* a log left behind is harmless */ }
        return Task.FromResult(new Outcome(new { runId = id, deleted = true }));
    }
}
