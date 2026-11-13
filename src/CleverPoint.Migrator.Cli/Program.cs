using CleverPoint.Migrator.Cli.Commands;

namespace CleverPoint.Migrator.Cli;

/// <summary>
/// cpm: the command line of CleverPoint Migrator.
///
/// It exists so that scripts and AI agents can run migrations without the desktop app: hand it a job
/// file, get a job ID back, poll for progress, read the failures. It is a thin shell over the same
/// engine, run service, connections and history the desktop app uses.
/// </summary>
public static class Program
{
    private static readonly List<Command> Commands = Build();

    private static List<Command> Build()
    {
        var commands = new List<Command>();
        commands.AddRange(AgentCommands.All(() => Commands));
        commands.AddRange(ConnectionCommands.All());
        commands.AddRange(SiteCommands.All());
        commands.AddRange(JobCommands.All());
        commands.AddRange(RunCommands.All());
        commands.AddRange(ReportCommands.All());
        return commands;
    }

    public static Task<int> Main(string[] args) => Runner.RunAsync(args, Commands);
}
