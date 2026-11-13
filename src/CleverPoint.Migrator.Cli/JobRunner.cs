using System.Diagnostics;
using System.Text;
using CleverPoint.Migrator.Core.Auth;
using CleverPoint.Migrator.Core.Csom;
using CleverPoint.Migrator.Core.History;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Cli;

/// <summary>One list of a job, matched to what the source site actually has.</summary>
public sealed record PlannedList(JobList Spec, string SourceTitle, string SourcePath, bool IsLibrary, int Items)
{
    /// <summary>The target list as the target site has it, or null when the job will create it.</summary>
    public SpListInfo? Target { get; init; }

    /// <summary>
    /// Engine options for this list. When the target exists, its real title is used, whatever the job
    /// called it: history records and pairs runs by that title, the same way the desktop app does.
    /// </summary>
    public Core.Model.CopyOptions Options(JobSpec spec)
    {
        var options = JobSpecs.ToCopyOptions(spec, Spec, SourceTitle);
        // A new plain list goes under Lists/, where SharePoint itself puts lists, unless the job says otherwise.
        if (Target is null && !IsLibrary && string.IsNullOrWhiteSpace(Spec.TargetUrl))
            options.TargetListUrl = "Lists/" + options.TargetListUrl;
        if (Target is not null)
        {
            options.TargetListTitle = Target.Title;
            options.TargetListServerRelativeUrl = Target.ServerRelativeUrl;
        }
        return options;
    }
}

/// <summary>
/// Executes a job: one run per list, in order, through the same run service the desktop app uses.
///
/// A job normally executes in its own detached process, started by "cpm job start", so the command
/// that started it returns at once and the job outlives the shell or agent session that asked for it.
/// Everything a caller wants to know afterwards comes from the history database.
/// </summary>
public static class JobRunner
{
    public static string NewJobId() =>
        $"j{DateTime.UtcNow:yyMMdd}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";

    public static string LogPath(string jobId) => Path.Combine(UxSettings.Folder, "logs", $"job-{jobId}.log");

    /// <summary>
    /// Matches the job's lists to the source site. Every list has to be found before anything runs, so a
    /// typo fails the whole job up front instead of after the first three lists were copied.
    /// </summary>
    public static async Task<List<PlannedList>> PlanAsync(JobSpec spec, Session session, SpConnection source, SpConnection target)
    {
        var planned = await PlanSourcesAsync(spec, session, source);

        // Then the targets. A targetPath is a promise that the list exists; when it does not, creating
        // some other list from the title would put the content somewhere the job did not say.
        var existing = await session.Sites.GetListsAsync(target, useCache: false);
        var absent = new List<string>();
        for (var i = 0; i < planned.Count; i++)
        {
            var list = planned[i];
            SpListInfo? match;
            if (!string.IsNullOrWhiteSpace(list.Spec.TargetPath))
            {
                match = existing.FirstOrDefault(l => Norm(l.ServerRelativeUrl) == Norm(list.Spec.TargetPath));
                if (match is null) absent.Add(list.Spec.TargetPath!);
            }
            else
            {
                var title = string.IsNullOrWhiteSpace(list.Spec.TargetTitle) ? list.SourceTitle : list.Spec.TargetTitle!.Trim();
                match = existing.FirstOrDefault(l => l.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            }
            planned[i] = list with { Target = match };
        }
        if (absent.Count > 0)
            throw CliError.NotFound($"No list at this targetPath on the target site: {string.Join(", ", absent)}.",
                $"targetPath is for a list that already exists (see: cpm site lists --site {spec.Target}). To create a list, give targetTitle and optionally targetUrl instead.");
        return planned;
    }

    private static string Norm(string? path) => Uri.UnescapeDataString(path ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    private static async Task<List<PlannedList>> PlanSourcesAsync(JobSpec spec, Session session, SpConnection source)
    {
        var available = await session.Sites.GetListsAsync(source, useCache: false);

        if (spec.AllLists)
        {
            var excluded = spec.ExcludeLists.Select(Norm).ToHashSet();
            return available
                .Where(l => !excluded.Contains(Norm(l.Title)) && !excluded.Contains(Norm(l.ServerRelativeUrl)))
                .Select(l => new PlannedList(new JobList { Source = l.Title, SourcePath = l.ServerRelativeUrl }, l.Title, l.ServerRelativeUrl, l.IsLibrary, l.ItemCount))
                .ToList();
        }

        var planned = new List<PlannedList>();
        var missing = new List<string>();
        foreach (var list in spec.Lists)
        {
            var match = (!string.IsNullOrWhiteSpace(list.SourcePath) ? available.FirstOrDefault(l => Norm(l.ServerRelativeUrl) == Norm(list.SourcePath)) : null)
                        ?? (!string.IsNullOrWhiteSpace(list.Source) ? available.FirstOrDefault(l => l.Title.Equals(list.Source!.Trim(), StringComparison.OrdinalIgnoreCase)) : null);
            if (match is null) missing.Add(list.SourcePath ?? list.Source ?? "(unnamed)");
            else planned.Add(new PlannedList(list, match.Title, match.ServerRelativeUrl, match.IsLibrary, match.ItemCount));
        }
        if (missing.Count > 0)
            throw CliError.NotFound($"Not found on the source site: {string.Join(", ", missing)}.",
                $"See what the site has with: cpm site lists --site {spec.Source}");
        return planned;
    }

    /// <summary>Starts "cpm job exec" as a process that is not tied to this one, its console or its pipes.</summary>
    public static void SpawnDetached(string jobId) => SpawnDetached("job", "exec", jobId);

    /// <summary>Starts a cpm command (a hidden "exec" one) as a process that is not tied to this one, its console or its pipes.</summary>
    public static void SpawnDetached(params string[] command)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the cpm executable to start the job.");
        var arguments = new List<string>();
        // Started as "dotnet cpm.dll" (development): the child needs the dll as its first argument.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        arguments.AddRange(command);

        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // Through the shell, so no handle of this process is inherited. A child that inherited our
            // stdout would keep the caller's pipe open, and whoever started us would wait for the whole
            // job instead of getting the job ID back.
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                WorkingDirectory = UxSettings.Folder,
            };
        }
        else
        {
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = UxSettings.Folder,
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        }
        Directory.CreateDirectory(UxSettings.Folder);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The job process could not be started.");
        if (!OperatingSystem.IsWindows()) process.StandardInput.Close();
    }

    /// <summary>The body of "cpm job exec": runs the job to the end and records how it ended.</summary>
    public static async Task ExecuteAsync(string jobId, bool detached)
    {
        if (detached)
        {
            // Nobody reads this process's console; everything goes to the job's log file.
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath(jobId))!);
            var log = new StreamWriter(LogPath(jobId), append: true, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }
        void Log(string line) => Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");

        MigrationJob? job;
        using (var store = Session.OpenHistory())
        {
            job = store.GetJob(jobId);
            if (job is null || !store.StartJob(jobId))
            {
                Log(job is null ? $"job {jobId} not found" : $"job {jobId} is {job.Status}; nothing to do");
                return;
            }
        }

        using var cancel = new CancellationTokenSource();
        using var heartbeat = new Timer(_ =>
        {
            try
            {
                using var store = Session.OpenHistory();
                if (store.JobHeartbeat(jobId)) cancel.Cancel();
            }
            catch { /* a busy database must not stop the job */ }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        var status = "Completed";
        string? error = null;
        try
        {
            var issues = new List<JobIssue>();
            var spec = JobSpecs.Parse(job.SpecJson, issues) ?? throw new InvalidOperationException(issues[0].Message);
            var session = new Session();
            var service = new UxMigrationService(session.Settings, session.Browser) { AllowInteractiveSignIn = false };
            var source = await session.ConnectAsync(spec.Source);
            var target = await session.ConnectAsync(spec.Target);
            var lists = await PlanAsync(spec, session, source, target);
            var mapping = JobSpecs.ResolveMapping(spec);
            Log($"job {jobId}: {lists.Count} list(s), mode {spec.Mode}, {spec.Source} -> {spec.Target}");

            // The timer above notices a cancel within ten seconds. Between lists that is not good
            // enough: the run that was in progress stops within two, and the next list must not start.
            bool Cancelled()
            {
                if (cancel.IsCancellationRequested) return true;
                try
                {
                    using var store = Session.OpenHistory();
                    if (store.JobHeartbeat(jobId)) cancel.Cancel();
                }
                catch { /* a busy database: the timer will catch up */ }
                return cancel.IsCancellationRequested;
            }

            var outcomes = new List<string>();
            foreach (var list in lists)
            {
                if (Cancelled()) break;
                var options = list.Options(spec);
                // The run service switches a list to the Classic engine when the Migration API cannot
                // do what the job asks for it, and notes why in the run log.
                var engine = JobSpecs.EngineName(spec);
                var name = string.IsNullOrWhiteSpace(spec.Name) ? null : lists.Count == 1 ? spec.Name : $"{spec.Name}: {list.SourceTitle}";
                Log($"start '{list.SourceTitle}' -> '{options.TargetListTitle}' ({engine})");
                var (result, runStatus) = await service.RunAsync(spec.Source, spec.Target, list.SourceTitle, options, _ => { },
                    cancel.Token, engine, name, sourceIsLibrary: list.IsLibrary, jobId: jobId, mappingOverride: mapping);
                Log($"done '{list.SourceTitle}': {runStatus}, {result.Summary()}");
                outcomes.Add(runStatus);
            }

            if (Cancelled()) status = "Cancelled";
            else if (outcomes.Count > 0 && outcomes.All(o => o == "Failed")) status = "Failed";
            else if (outcomes.Any(o => o != "Completed")) status = "CompletedWithIssues";
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
        }
        catch (Exception ex)
        {
            // The same wording a foreground command would give, so `cpm job status` reads the same.
            var translated = Runner.Translate(ex);
            status = "Failed";
            error = $"{translated.Code}: {translated.Message}" + (translated.Hint is null ? "" : " " + translated.Hint);
            if (ex is not CliError and not SignInRequiredException) Log(ex.ToString());
        }
        finally
        {
            heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
            using var store = Session.OpenHistory();
            store.FinishJob(jobId, status, error);
            Log($"job {jobId} finished: {status}" + (error is null ? "" : $" ({error})"));
        }
    }
}
