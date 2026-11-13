using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>
/// Runs: one list copied once. A job makes one run per list; the desktop app makes runs too, and they
/// are all in the same history, so these commands see both.
/// </summary>
public static class RunCommands
{
    public static IEnumerable<Command> All() => new[]
    {
        new Command
        {
            Path = new[] { "run", "list" },
            Summary = "List recent runs, from jobs and from the desktop app.",
            Options = new[] { new Opt("limit", "<n>", "Most runs to return. Default 20.") },
            Run = List,
        },
        new Command
        {
            Path = new[] { "run", "status" },
            Summary = "Show one run's state and live counts.",
            Args = new[] { "<runId>" },
            Run = Status,
        },
        new Command
        {
            Path = new[] { "run", "items" },
            Summary = "Show what happened to individual items in a run. Use it to read failures and warnings.",
            Args = new[] { "<runId>" },
            Options = new[]
            {
                new Opt("status", "<list>", "Comma-separated: failed, warning, skipped, copied, info. Default: failed,warning."),
                new Opt("limit", "<n>", "Most rows to return. Default 50."),
                new Opt("after", "<rowId>", "Continue after this row ID (the `nextAfter` of the previous page)."),
            },
            Examples = new[] { "cpm run items 42", "cpm run items 42 --status skipped --limit 200" },
            Run = Items,
        },
        new Command
        {
            Path = new[] { "run", "cancel" },
            Summary = "Ask a running run to stop, whether a job or the desktop app started it.",
            Args = new[] { "<runId>" },
            Run = Cancel,
        },
        new Command
        {
            Path = new[] { "run", "export" },
            Summary = "Write a run's complete item log to a CSV file.",
            Args = new[] { "<runId>" },
            Options = new[] { new Opt("out", "<file>", "CSV file to write.", Required: true) },
            Run = Export,
        },
        new Command
        {
            Path = new[] { "report", "verify" },
            Summary = "Compare a finished run's source and target and write a verification report (CSV).",
            Args = new[] { "<runId>" },
            Options = new[] { new Opt("deep", null, "Also compare the contents of a sample of files (1 in 10). Slower.") },
            Notes = "Reads both sites again; changes nothing. A long list takes a while.",
            Run = VerifyAsync,
        },
    };

    /// <summary>A run with counts taken from its item rows, so they are current while it is still going.</summary>
    public static object Describe(HistoryStore store, MigrationRun run)
    {
        var progress = store.GetRunProgress(run.Id);
        return new
        {
            runId = run.Id,
            name = run.Name,
            status = run.Status,
            source = run.SourceList,
            target = run.TargetList,
            engine = run.Engine,
            planned = progress.Planned == 0 ? (int?)null : progress.Planned,
            processed = progress.Processed,
            percent = progress.Planned > 0 ? Math.Min(100, progress.Processed * 100 / progress.Planned) : (int?)null,
            copied = progress.Copied,
            skipped = progress.Skipped,
            warnings = progress.Warnings,
            failed = progress.Failed,
            lastItem = run.Status == "Running" ? progress.LastItem : null,
            started = run.StartedUtc,
            finishedAt = run.FinishedUtc,
            jobId = run.JobId,
        };
    }

    private static MigrationRun Find(HistoryStore store, long id)
    {
        store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        return store.GetRun(id) ?? throw CliError.NotFound($"No run {id}.", "List runs with: cpm run list");
    }

    private static Task<Outcome> List(Invocation args)
    {
        using var store = Session.OpenHistory();
        store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        var runs = store.GetRuns(Math.Clamp(args.Int("limit", 20), 1, 500));
        return Task.FromResult(new Outcome(new
        {
            runs = runs.Select(r => new
            {
                runId = r.Id, name = r.Name, status = r.Status, source = r.SourceList, target = r.TargetList,
                sourceSite = r.SourceUrl, targetSite = r.TargetUrl, started = r.StartedUtc, failed = r.Failed, jobId = r.JobId,
            }).ToList(),
        }));
    }

    private static Task<Outcome> Status(Invocation args)
    {
        using var store = Session.OpenHistory();
        var run = Find(store, args.Long(0, "<runId>"));
        var exit = run.Status switch
        {
            "Completed" or "Running" => Exit.Ok,
            "Interrupted" => Exit.Cancelled,
            _ => Exit.CompletedWithIssues,
        };
        return Task.FromResult(new Outcome(new
        {
            run = Describe(store, run),
            sourceSite = run.SourceUrl,
            targetSite = run.TargetUrl,
            next = run.Failed > 0 || run.Warnings > 0 ? $"cpm run items {run.Id} --status failed,warning" : null,
        }, exit));
    }

    private static readonly Dictionary<string, string> StatusNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["failed"] = "Failed", ["warning"] = "Warning", ["warnings"] = "Warning", ["skipped"] = "Skipped", ["copied"] = "Copied", ["info"] = "Info",
    };

    private static Task<Outcome> Items(Invocation args)
    {
        using var store = Session.OpenHistory();
        var run = Find(store, args.Long(0, "<runId>"));
        var wanted = (args.Get("status") ?? "failed,warning").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var statuses = new List<string>();
        foreach (var name in wanted)
        {
            if (name.Equals("all", StringComparison.OrdinalIgnoreCase)) { statuses.Clear(); break; }
            statuses.Add(StatusNames.TryGetValue(name, out var canonical)
                ? canonical
                : throw CliError.Usage($"Unknown status '{name}'.", "Use: failed, warning, skipped, copied, info, or all."));
        }
        var limit = Math.Clamp(args.Int("limit", 50), 1, 5000);
        long after = 0;
        if (args.Get("after") is { } afterText && !long.TryParse(afterText, out after))
            throw CliError.Usage("--after needs the row ID from `nextAfter`.");

        // One extra row tells whether there is another page.
        var rows = store.GetItemsPage(run.Id, statuses.Count == 0 ? null : statuses, limit + 1, after);
        var more = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        return Task.FromResult(new Outcome(new
        {
            runId = run.Id,
            status = run.Status,
            returned = page.Count,
            nextAfter = more ? page[^1].Id : (long?)null,
            items = page.Select(i => new
            {
                row = i.Id,
                type = i.ItemType,
                status = i.Status,
                source = i.SourcePath,
                target = string.IsNullOrEmpty(i.TargetPath) ? null : i.TargetPath,
                message = i.Message,
            }).ToList(),
        }));
    }

    private static Task<Outcome> Cancel(Invocation args)
    {
        using var store = Session.OpenHistory();
        var run = Find(store, args.Long(0, "<runId>"));
        var requested = store.RequestCancelRun(run.Id);
        return Task.FromResult(new Outcome(new
        {
            runId = run.Id,
            cancelRequested = requested,
            message = requested ? "The run stops after the item it is working on." : $"The run is already {run.Status}.",
        }));
    }

    private static Task<Outcome> Export(Invocation args)
    {
        using var store = Session.OpenHistory();
        var run = Find(store, args.Long(0, "<runId>"));
        var path = Session.OutputFile(args.Get("out")!);
        store.ExportRunCsv(run.Id, path);
        return Task.FromResult(new Outcome(new { runId = run.Id, file = path }));
    }

    private static async Task<Outcome> VerifyAsync(Invocation args)
    {
        MigrationRun run;
        using (var store = Session.OpenHistory()) run = Find(store, args.Long(0, "<runId>"));
        if (run.Status == "Running")
            throw CliError.Invalid($"Run {run.Id} is still running.", $"Wait for it, then verify: cpm run status {run.Id}");

        var session = new Session();
        // Proves both sign-ins first, so an expired one comes back as a sign-in error, not a failed report.
        await session.ConnectAsync(run.SourceUrl);
        await session.ConnectAsync(run.TargetUrl);
        var service = new UxMigrationService(session.Settings, session.Browser) { AllowInteractiveSignIn = false };
        var (report, path) = await service.VerifyRunAsync(run, deep: args.Has("deep"), onProgress: Runner.Note);
        return new Outcome(new
        {
            runId = run.Id,
            clean = report.IsClean,
            verdict = report.Verdict,
            mismatches = report.Mismatches.Count,
            firstMismatches = report.Mismatches.Take(20).ToList(),
            report = path,
        }, report.IsClean ? Exit.Ok : Exit.CompletedWithIssues);
    }
}
