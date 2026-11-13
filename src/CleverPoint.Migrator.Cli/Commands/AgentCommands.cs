using System.Reflection;
using System.Text;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Cli.Commands;

/// <summary>
/// How an AI agent learns this tool. The guide is generated here, from the command table and the job
/// classes of the installed version, so it never describes a command this version does not have.
/// </summary>
public static class AgentCommands
{
    public static string ToolVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentCommands).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
            return version.Split('+')[0];
        }
    }

    public static IEnumerable<Command> All(Func<IReadOnlyList<Command>> commands) => new[]
    {
        new Command
        {
            Path = new[] { "agent", "guide" },
            Summary = "Print how to use this tool: the workflow, every command, the job file and the exit codes.",
            Options = new[]
            {
                new Opt("skill-version", "<n>", "The skill-version line of the skill file you were given. Tells you when that file is outdated."),
                new Opt("json", null, "The command list and capabilities as JSON instead of text."),
            },
            Run = args => Task.FromResult(Guide(args, commands())),
        },
        new Command
        {
            Path = new[] { "agent", "install" },
            Summary = "Install the skill file for the AI agents found on this computer (and in WSL, when present).",
            Options = new[]
            {
                new Opt("agent", "<list>", "Only these agents, comma-separated: claude, codex, gemini, agents. Default: every one whose folder exists."),
                new Opt("no-wsl", null, "On Windows: leave WSL distributions alone."),
                new Opt("refresh", null, "Only update copies installed earlier, and only those nobody has edited. Installs nowhere new."),
            },
            Notes = "The skill is a short pointer to `cpm agent guide`. It tells an agent this tool exists and when to use it.",
            Run = Install,
        },
        new Command
        {
            Path = new[] { "agent", "status" },
            Summary = "Show where the skill file is installed and whether each copy is current.",
            Run = _ => Task.FromResult(new Outcome(new
            {
                skillVersion = AgentSkill.Version,
                installed = AgentSkill.Status().Select(Describe).ToList(),
                agentsFound = AgentSkill.Discover().Select(d => new { target = d.Target, agent = d.Agent, path = d.Path }).ToList(),
            })),
        },
        new Command
        {
            Path = new[] { "version" },
            Summary = "Print the version.",
            Run = _ => Task.FromResult(new Outcome(new { version = ToolVersion, skillVersion = AgentSkill.Version, jobSchemaVersion = JobSpec.CurrentSchemaVersion })),
        },
    };

    private static object Describe(SkillLocation location) => new
    {
        target = location.Target, agent = location.Agent, path = location.Path, state = location.State,
    };

    private static Task<Outcome> Install(Invocation args)
    {
        if (args.Has("refresh"))
            return Task.FromResult(new Outcome(new { skillVersion = AgentSkill.Version, refreshed = AgentSkill.Refresh().Select(Describe).ToList() }));

        var only = args.Get("agent")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var installed = AgentSkill.Install(only, includeWsl: !args.Has("no-wsl"));
        return Task.FromResult(new Outcome(new
        {
            skillVersion = AgentSkill.Version,
            installed = installed.Select(Describe).ToList(),
            message = installed.Count == 0
                ? "No AI agent folders were found (.claude, .codex, .gemini or .agents in the home folder). Nothing was installed; an agent can still learn the tool from: cpm agent guide"
                : "Agents pick the skill up in their next session.",
        }));
    }

    private static Outcome Guide(Invocation args, IReadOnlyList<Command> commands)
    {
        var holding = args.Get("skill-version") is { } text && int.TryParse(text, out var parsed) ? parsed : (int?)null;
        var outdated = holding is { } held && held < AgentSkill.Version;
        var visible = commands.Where(c => !c.Hidden).ToList();

        if (args.Has("json"))
            return new Outcome(new
            {
                version = ToolVersion,
                skillVersion = AgentSkill.Version,
                skillOutdated = outdated,
                jobSchemaVersion = JobSpec.CurrentSchemaVersion,
                commands = visible.Select(c => new
                {
                    name = c.Name,
                    usage = c.Usage,
                    summary = c.Summary,
                    interactive = c.Interactive ? true : (bool?)null,
                    options = c.Options.Select(o => new { name = "--" + o.Name, value = o.ValueName, help = o.Help, required = o.Required ? true : (bool?)null }).ToList(),
                }).ToList(),
                reports = Core.Reports.ReportCatalog.All.Select(r => new
                {
                    id = r.Id,
                    summary = r.Summary,
                    scope = Core.Reports.ReportCatalog.ScopeName(r.Scope),
                    needsTenantAdmin = r.NeedsTenantAdmin ? true : (bool?)null,
                    changesData = r.ChangesData ? true : (bool?)null,
                }).ToList(),
                exitCodes = Exit.All.Select(e => new { code = e.Code, meaning = e.Meaning }).ToList(),
            });

        var sb = new StringBuilder();
        if (outdated)
        {
            sb.AppendLine($"YOUR SKILL FILE IS OUTDATED (it is version {holding}, this tool ships version {AgentSkill.Version}).");
            sb.AppendLine("Run `cpm agent install` once to update it, then continue. This guide is current either way.");
            sb.AppendLine();
        }
        sb.AppendLine($"# CleverPoint Migrator command line (cpm {ToolVersion})");
        sb.AppendLine();
        sb.AppendLine("Copies SharePoint Online lists, libraries, folders, files, site pages and permissions between sites and");
        sb.AppendLine("tenants. You describe the work in a job file; the tool does the copying, retries throttling, and records");
        sb.AppendLine("what happened to every item. It also runs reports over any number of sites (see Reports below).");
        sb.AppendLine("It shares connections and history with the CleverPoint Migrator desktop app.");
        sb.AppendLine();
        sb.AppendLine("## Workflow (copying)");
        sb.AppendLine();
        sb.AppendLine("1. `cpm connection list`. The source and the target site each need a connection with `ready: true`.");
        sb.AppendLine("   Not ready, or exit code 3: the user has to run `cpm connection login --site <url>` themselves (it opens");
        sb.AppendLine("   their browser). No connection at all: `cpm connection add`, or for a tenant with no app yet an");
        sb.AppendLine("   administrator runs `cpm connection setup --site <url>` once.");
        sb.AppendLine("2. `cpm site lists --site <source url>`. Take list titles and paths from here; do not guess them.");
        sb.AppendLine("3. Write a job file (JSON). Start from `cpm job template --raw`. Fields are listed below.");
        sb.AppendLine("4. `cpm job validate <file> --online`. It changes nothing and shows what each list would do.");
        sb.AppendLine("5. `cpm job start <file>`. Returns a jobId immediately; the job runs in the background.");
        sb.AppendLine("6. `cpm job wait <jobId> --timeout 600`. Exit code 6 means still running: wait again. Or poll `cpm job status <jobId>`.");
        sb.AppendLine("7. When a run has failures or warnings: `cpm run items <runId>`. Read them before telling the user the job is done.");
        sb.AppendLine("8. Optional proof: `cpm report verify <runId>` compares source and target and writes a CSV report.");
        sb.AppendLine();
        sb.AppendLine("## Reports");
        sb.AppendLine();
        sb.AppendLine("A report scans many sites at once in the background and keeps its rows on disk, so it can cover a whole tenant.");
        sb.AppendLine();
        var width = Core.Reports.ReportCatalog.All.Max(r => r.Id.Length) + 2;
        foreach (var report in Core.Reports.ReportCatalog.All)
            sb.AppendLine($"  {report.Id.PadRight(width)}[{Core.Reports.ReportCatalog.ScopeName(report.Scope)}{(report.NeedsTenantAdmin ? ", needs the admin site" : "")}{(report.ChangesData ? ", CHANGES SITES" : "")}] {report.Summary}");
        sb.AppendLine();
        sb.AppendLine("1. `cpm report list --report <id>` for a report's settings and columns.");
        sb.AppendLine("2. Say what to scan. Scope sites: `--sites a,b`, `--sites-file file` (one URL per line), `--all-sites <any site of the tenant>`,");
        sb.AppendLine("   or `--from-report <runId of a sites run>`. Scope tenant: `--tenant <any site of the tenant>`.");
        sb.AppendLine("   Not sure which sites exist? Run the `sites` report first and feed its run ID to `--from-report`.");
        sb.AppendLine("3. `cpm report start --report <id> ...` returns a runId at once. `cpm report wait <runId> --timeout 600` blocks;");
        sb.AppendLine("   exit code 6 means still running. `cpm report status <runId>` shows sites done and failed, rows and summary numbers.");
        sb.AppendLine("4. Read results with `cpm report rows <runId> --where ... --columns ... --limit 50`. Start from the summary in status;");
        sb.AppendLine("   a report can hold millions of rows, so never page through all of it. For the user, `cpm report export <runId>` writes Excel or CSV.");
        sb.AppendLine("5. Sites that could not be scanned do not stop a run. See them with `cpm report sites <runId> --failed`.");
        sb.AppendLine("   `cpm report resume <runId>` tries those again and continues a run that was cancelled or interrupted, without redoing what is done.");
        sb.AppendLine();
        sb.AppendLine("Actions (marked CHANGES SITES) are started the same way and then only preview: the rows say what would change.");
        sb.AppendLine("Show that to the user. `cpm report apply <previewRunId>` makes the changes, and only after the user agreed to them.");
        sb.AppendLine();
        sb.AppendLine("Tenant-wide reports and --all-sites need a connection that reaches the tenant's admin site: an app + certificate");
        sb.AppendLine("connection, or a Microsoft sign-in by a SharePoint administrator.");
        sb.AppendLine();
        sb.AppendLine("A report can also be a file, like a job: `cpm report template <id> --raw` to start one, `cpm schema report` for its fields.");
        sb.AppendLine();
        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine("Every command prints one JSON object on stdout:");
        sb.AppendLine("  success: {\"ok\":true,\"data\":{...}}        failure: {\"ok\":false,\"error\":{\"code\":\"...\",\"message\":\"...\",\"hint\":\"...\"}}");
        sb.AppendLine("The hint names the command that fixes the problem. Many results carry `next`: the command to run next.");
        sb.AppendLine("Add --pretty to indent. Nothing ever prompts for input.");
        sb.AppendLine();
        sb.AppendLine("## Commands");
        sb.AppendLine();
        // Two commands that share a long set of options (start and validate) list it once.
        var listed = new List<(string Command, HashSet<string> Options)>();
        foreach (var command in visible)
        {
            sb.AppendLine($"{command.Usage}{(command.Interactive ? "    [opens a browser; the user must run it]" : "")}");
            sb.AppendLine($"    {command.Summary}");
            var options = command.Options.Where(o => !o.Required).ToList();
            var shared = listed.FirstOrDefault(l => options.Count(o => l.Options.Contains(o.Name + o.Help)) >= 8);
            foreach (var option in options.Where(o => shared.Options is null || !shared.Options.Contains(o.Name + o.Help)))
                sb.AppendLine($"    {Help.OptionLabel(option)}: {option.Help}");
            if (shared.Options is not null) sb.AppendLine($"    Also takes the options of: cpm {shared.Command}");
            else if (options.Count >= 8) listed.Add((command.Name, options.Select(o => o.Name + o.Help).ToHashSet()));
        }
        sb.AppendLine();
        sb.AppendLine("## Job file");
        sb.AppendLine();
        sb.AppendLine("JSON. Unknown settings are rejected, so a typo is an error and not a silent no-op. The same as a schema: `cpm schema job`.");
        sb.AppendLine();
        sb.Append(SchemaGenerator.Describe(typeof(JobSpec)));
        sb.AppendLine();
        sb.AppendLine("Smallest useful job:");
        sb.AppendLine("  {\"source\":\"https://contoso.sharepoint.com/sites/A\",\"target\":\"https://contoso.sharepoint.com/sites/B\",\"lists\":[{\"source\":\"Documents\"}]}");
        sb.AppendLine();
        sb.AppendLine("## Exit codes");
        sb.AppendLine();
        foreach (var (code, meaning) in Exit.All) sb.AppendLine($"{code,2}  {meaning}");
        sb.AppendLine();
        sb.AppendLine("## Things to know");
        sb.AppendLine();
        sb.AppendLine("- Running a job again is safe. Files pair by path and list items by the pairs recorded in history, so a");
        sb.AppendLine("  second run updates instead of duplicating. Use options.existing = copyIfNewer or skip for a quick top-up.");
        sb.AppendLine("- mode permissions copies nothing. It gives every source object that has its own permissions the same");
        sb.AppendLine("  permissions on its counterpart, replacing what that target object has. List items only pair if this tool");
        sb.AppendLine("  copied the list earlier. Tell the user before running it.");
        sb.AppendLine("- Users are matched by sign-in name and email. Across tenants, make a mapping: `cpm mapping template`,");
        sb.AppendLine("  have the user fill in the empty targets, and name the file in identityMapping.csv.");
        sb.AppendLine("- A job touches only the target site. The source is only read.");
        sb.AppendLine("- Runs started here appear live in the desktop app's Activity page, and `cpm run list` shows the app's runs.");
        sb.AppendLine("- Secrets never go on the command line or in a job file. Do not ask the user for passwords.");
        sb.AppendLine("- On WSL the tool is a Windows program: call `cpm.exe` and pass Windows paths (`wslpath -w ./job.json`).");
        sb.AppendLine("- `cpm <command> --help` shows one command's options and examples.");

        // The guide is prose for a reader, so it is printed as it is instead of inside the JSON wrapper.
        Console.Out.Write(sb.ToString());
        Environment.Exit(Exit.Ok);
        return new Outcome(null);
    }
}
