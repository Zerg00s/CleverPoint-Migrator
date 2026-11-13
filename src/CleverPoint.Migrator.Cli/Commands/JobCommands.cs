using System.Text.Json;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>Jobs: a job file in, a job ID out, then status until it is done.</summary>
public static class JobCommands
{
    public static IEnumerable<Command> All() => new[]
    {
        new Command
        {
            Path = new[] { "schema", "job" },
            Summary = "Print the JSON Schema of the job file: every setting, its type, default and meaning.",
            Options = new[] { new Opt("raw", null, "Print the schema alone, without the ok/data wrapper, for saving to a file.") },
            Run = args => Task.FromResult(Raw(args, SchemaGenerator.For(typeof(JobSpec), "CleverPoint Migrator job"))),
        },
        new Command
        {
            Path = new[] { "job", "template" },
            Summary = "Print an example job file to start from.",
            Options = new[] { new Opt("raw", null, "Print the job alone, without the ok/data wrapper, for saving to a file.") },
            Run = args => Task.FromResult(Raw(args, JsonSerializer.SerializeToElement(JobSpecs.Example(), JobSpecs.Json))),
        },
        new Command
        {
            Path = new[] { "job", "validate" },
            Summary = "Check a job file. With --online, also check it against the real sites and show what would happen.",
            Args = new[] { "<file>" },
            Options = new[] { new Opt("online", null, "Also sign in silently, find each list on the source and look at its target. Changes nothing.") },
            Examples = new[] { "cpm job validate job.json --online" },
            Run = ValidateAsync,
        },
        new Command
        {
            Path = new[] { "job", "start" },
            Summary = "Start a job in the background and return its job ID at once.",
            Args = new[] { "<file>" },
            Options = new[]
            {
                new Opt("wait", null, "Block until the job finishes (or --timeout passes) and return its final status."),
                new Opt("timeout", "<seconds>", "With --wait: give up waiting after this long. The job keeps running. Default 3600."),
            },
            Notes = "The job runs in its own process and survives this shell. Follow it with `cpm job status <jobId>` or `cpm job wait <jobId>`.",
            Examples = new[] { "cpm job start job.json", "cpm job start job.json --wait --timeout 600" },
            Run = StartAsync,
        },
        new Command
        {
            Path = new[] { "job", "status" },
            Summary = "Show a job's state and the progress of each of its runs.",
            Args = new[] { "<jobId>" },
            Run = args => Task.FromResult(StatusOutcome(args.Arg(0, "<jobId>"))),
        },
        new Command
        {
            Path = new[] { "job", "wait" },
            Summary = "Block until a job finishes, then show its final status.",
            Args = new[] { "<jobId>" },
            Options = new[] { new Opt("timeout", "<seconds>", "Give up waiting after this long (exit code 6). The job keeps running. Default 3600.") },
            Run = args => WaitAsync(args.Arg(0, "<jobId>"), args.Int("timeout", 3600)),
        },
        new Command
        {
            Path = new[] { "job", "cancel" },
            Summary = "Ask a running job to stop. Items already copied stay copied.",
            Args = new[] { "<jobId>" },
            Run = Cancel,
        },
        new Command
        {
            Path = new[] { "job", "list" },
            Summary = "List recent jobs.",
            Options = new[] { new Opt("limit", "<n>", "Most jobs to return. Default 20.") },
            Run = List,
        },
        new Command
        {
            Path = new[] { "job", "exec" },
            Summary = "Execute a queued job in this process.",
            Args = new[] { "<jobId>" },
            Options = new[] { new Opt("foreground", null, "Keep logging to this console instead of the job's log file.") },
            Hidden = true,
            Run = async args =>
            {
                var detached = !args.Has("foreground");
                await JobRunner.ExecuteAsync(args.Arg(0, "<jobId>"), detached);
                // Detached, the console is the job's log file: it gets the job's own lines and nothing else.
                if (detached) Environment.Exit(Exit.Ok);
                return StatusOutcome(args.Arg(0, "<jobId>"));
            },
        },
    };

    private static Outcome Raw(Invocation args, JsonElement document)
    {
        if (!args.Has("raw")) return new Outcome(document);
        // The one exception to "one wrapped JSON object": the caller asked for the bare document.
        Console.Out.WriteLine(JsonSerializer.Serialize(document, new JsonSerializerOptions(Runner.Json) { WriteIndented = true }));
        Environment.Exit(Exit.Ok);
        return new Outcome(null);
    }

    private static (JobSpec Spec, string Json) Load(string file)
    {
        if (!File.Exists(file)) throw CliError.NotFound($"Job file not found: {file}", "On WSL, pass a Windows path: wslpath -w ./job.json");
        var json = File.ReadAllText(file);
        var issues = new List<JobIssue>();
        var spec = JobSpecs.Parse(json, issues);
        if (spec is not null)
        {
            // A mapping file named relative to the job file means "next to the job file", wherever the
            // command happens to be run from. The background process runs somewhere else again, so
            // the job is stored with the full path.
            if (spec.IdentityMapping?.Csv is { Length: > 0 } csv && !Path.IsPathRooted(csv))
            {
                spec.IdentityMapping.Csv = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, csv));
                json = JsonSerializer.Serialize(spec, JobSpecs.Json);
            }
            issues.AddRange(JobSpecs.Validate(spec));
        }
        if (issues.Count > 0)
            throw CliError.Invalid($"The job file has {issues.Count} problem(s).", "Fix them and validate again. Every setting is described by: cpm schema job",
                issues.Select(i => new { at = i.Path, problem = i.Message }).ToList());
        return (spec!, json);
    }

    private static async Task<Outcome> ValidateAsync(Invocation args)
    {
        var (spec, _) = Load(args.Arg(0, "<file>"));
        var session = new Session();
        // Both connections have to exist even for an offline check: without them the job cannot start.
        var sourceConnection = session.Connection(spec.Source);
        var targetConnection = session.Connection(spec.Target);
        if (!args.Has("online"))
            return new Outcome(new
            {
                valid = true,
                checkedOnline = false,
                source = new { site = spec.Source, signIn = Session.SignInKind(sourceConnection), ready = session.IsReady(sourceConnection) },
                target = new { site = spec.Target, signIn = Session.SignInKind(targetConnection), ready = session.IsReady(targetConnection) },
                next = $"cpm job validate {args.Arg(0, "<file>")} --online",
            });

        var source = await session.ConnectAsync(spec.Source);
        var target = await session.ConnectAsync(spec.Target);
        var lists = await JobRunner.PlanAsync(spec, session, source, target);
        var mapping = JobSpecs.ResolveMapping(spec);
        var needsExisting = spec.Mode.ToLowerInvariant() is "content" or "permissions";
        var problems = new List<object>();
        var plan = new List<object>();
        foreach (var list in lists)
        {
            var options = list.Options(spec);
            var info = list.Target;
            // A hidden list does not show in the site's list of lists; ask for it by name before
            // concluding that the target has to be created.
            Guid? existing = Guid.Empty;
            if (info is null)
            {
                using var context = target.CreateContext();
                existing = await ListLocator.TryFindIdAsync(context, options.TargetListTitle, options.TargetListServerRelativeUrl);
            }
            string? problem = null;
            if (existing is null && needsExisting)
                problem = $"mode {spec.Mode} needs the target list to exist, and '{options.TargetListTitle}' does not. Copy it first with mode full.";
            else if (info is not null && info.IsLibrary != list.IsLibrary)
                problem = $"the source is a {(list.IsLibrary ? "library" : "list")} but the target '{info.Title}' is a {(info.IsLibrary ? "library" : "list")}.";
            if (problem is not null) problems.Add(new { list = list.SourceTitle, problem });
            var engineReasons = JobSpecs.EngineName(spec) == "MigrationApi"
                ? Core.MigrationApi.MigrationApiEngine.ReasonsToUseClassic(options, list.IsLibrary)
                : new List<string>();
            plan.Add(new
            {
                source = list.SourceTitle,
                sourcePath = list.SourcePath,
                type = list.IsLibrary ? "library" : "list",
                sourceItems = list.Items,
                target = options.TargetListTitle,
                targetPath = info?.ServerRelativeUrl,
                targetState = existing is null ? "willBeCreated" : "exists",
                targetItems = info?.ItemCount,
                engine = engineReasons.Count > 0 ? "Classic" : JobSpecs.EngineName(spec),
                engineNote = engineReasons.Count > 0 ? "Classic instead of the Migration API, because " + string.Join("; ", engineReasons) + "." : null,
            });
        }
        if (problems.Count > 0)
            throw CliError.Invalid($"The job would fail on {problems.Count} list(s).", "Fix the job file and validate again.", problems);
        return new Outcome(new
        {
            valid = true,
            checkedOnline = true,
            mode = spec.Mode,
            lists = plan,
            identityMapping = new { users = mapping.Users.Count, groups = mapping.Groups.Count, fallback = mapping.OrphanFallbackLogin },
            next = $"cpm job start {args.Arg(0, "<file>")}",
        });
    }

    private static async Task<Outcome> StartAsync(Invocation args)
    {
        var (spec, json) = Load(args.Arg(0, "<file>"));
        var session = new Session();
        // Fail now, with the command to fix it, rather than in a background process nobody is watching.
        await session.ConnectAsync(spec.Source);
        await session.ConnectAsync(spec.Target);

        var id = JobRunner.NewJobId();
        using (var store = Session.OpenHistory())
            store.CreateJob(new MigrationJob { Id = id, Name = spec.Name ?? Path.GetFileNameWithoutExtension(args.Arg(0, "<file>")), SpecJson = json, LogPath = JobRunner.LogPath(id) });
        JobRunner.SpawnDetached(id);

        if (args.Has("wait")) return await WaitAsync(id, args.Int("timeout", 3600));
        return new Outcome(new
        {
            jobId = id,
            status = "Queued",
            next = $"cpm job status {id}",
            orBlock = $"cpm job wait {id} --timeout 600",
        });
    }

    private static async Task<Outcome> WaitAsync(string jobId, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (true)
        {
            // Finished or not is read first and the status after, so a job that finishes between the
            // two is never reported as finished with the status of a moment before.
            bool finished;
            using (var store = Session.OpenHistory())
                finished = (store.GetJob(jobId) ?? throw CliError.NotFound($"No job {jobId}.", "List jobs with: cpm job list")).IsFinished;
            var outcome = StatusOutcome(jobId);
            if (finished) return outcome;
            if (DateTime.UtcNow >= deadline)
                return new Outcome(new { timedOut = true, job = outcome.Data, next = $"cpm job wait {jobId} --timeout {timeoutSeconds}" }, Exit.Timeout);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static Task<Outcome> Cancel(Invocation args)
    {
        var id = args.Arg(0, "<jobId>");
        using var store = Session.OpenHistory();
        var job = store.GetJob(id) ?? throw CliError.NotFound($"No job {id}.", "List jobs with: cpm job list");
        var requested = store.RequestCancelJob(id);
        return Task.FromResult(new Outcome(new
        {
            jobId = id,
            cancelRequested = requested,
            message = requested ? "The job stops after the item it is working on. Check with: cpm job status " + id : $"The job is already {job.Status}.",
        }));
    }

    private static Task<Outcome> List(Invocation args)
    {
        using var store = Session.OpenHistory();
        store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        var jobs = store.GetJobs(Math.Clamp(args.Int("limit", 20), 1, 500));
        return Task.FromResult(new Outcome(new
        {
            jobs = jobs.Select(j => new { jobId = j.Id, name = j.Name, status = j.Status, created = j.CreatedUtc, finished = j.FinishedUtc, error = j.Error }).ToList(),
        }));
    }

    /// <summary>A job's status and the exit code it implies.</summary>
    public static Outcome StatusOutcome(string jobId)
    {
        using var store = Session.OpenHistory();
        // A job whose process died would otherwise say "Running" forever.
        store.MarkOrphanedAsInterrupted(includeOwnProcess: false);
        var job = store.GetJob(jobId) ?? throw CliError.NotFound($"No job {jobId}.", "List jobs with: cpm job list");
        var runs = store.GetRunsOfJob(jobId).Select(r => RunCommands.Describe(store, r)).ToList();

        var totalLists = 0;
        try
        {
            var spec = JobSpecs.Parse(job.SpecJson, new List<JobIssue>());
            totalLists = spec is null || spec.AllLists ? 0 : spec.Lists.Count;
        }
        catch { /* the count is a convenience */ }

        var exit = job.Status switch
        {
            "Completed" or "Queued" or "Running" => Exit.Ok,
            "CompletedWithIssues" => Exit.CompletedWithIssues,
            "Cancelled" or "Interrupted" => Exit.Cancelled,
            _ => Exit.CompletedWithIssues,
        };
        var failedRun = store.GetRunsOfJob(jobId).FirstOrDefault(r => r.Failed > 0 || r.Status == "Failed");
        return new Outcome(new
        {
            jobId = job.Id,
            name = job.Name,
            status = job.Status,
            finished = job.IsFinished,
            error = job.Error,
            started = job.StartedUtc,
            finishedAt = job.FinishedUtc,
            elapsedSeconds = job.StartedUtc is { } started ? (int)((job.FinishedUtc ?? DateTime.UtcNow) - started).TotalSeconds : (int?)null,
            listsTotal = totalLists == 0 ? (int?)null : totalLists,
            listsStarted = runs.Count,
            runs,
            log = job.LogPath,
            next = !job.IsFinished ? $"cpm job wait {job.Id} --timeout 600"
                : failedRun is not null ? $"cpm run items {failedRun.Id} --status failed,warning"
                : null,
        }, exit);
    }
}
