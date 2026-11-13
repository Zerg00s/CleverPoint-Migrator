using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleverPoint.Migrator.Cli;

/// <summary>
/// Exit codes. Stable: scripts and agents branch on them.
/// </summary>
public static class Exit
{
    public const int Ok = 0;
    /// <summary>The command worked, but the job or run it reports on finished with failed items, or failed.</summary>
    public const int CompletedWithIssues = 1;
    public const int Usage = 2;
    public const int SignInRequired = 3;
    public const int NotFound = 4;
    public const int Invalid = 5;
    /// <summary>A wait ran out of time. The job is still running.</summary>
    public const int Timeout = 6;
    public const int Cancelled = 7;
    public const int Failed = 10;

    public static readonly (int Code, string Meaning)[] All =
    {
        (Ok, "Success."),
        (CompletedWithIssues, "The command worked, but the job or run finished with failed items or failed outright. Look at: cpm run items <runId> --status failed"),
        (Usage, "The command line is wrong: unknown command or option, or a missing value."),
        (SignInRequired, "A connection needs the user to sign in. Ask the user to run: cpm connection login --site <url>"),
        (NotFound, "The job, run, connection, list or file named does not exist."),
        (Invalid, "The job file or the request is not valid. The error lists what to fix."),
        (Timeout, "A wait ran out of time. The job is still running; wait again or check status."),
        (Cancelled, "The job was cancelled or its process stopped before it finished."),
        (Failed, "Something else went wrong. The error message says what."),
    };
}

/// <summary>An error the caller can act on: a stable code, a message, and the next command to try.</summary>
public sealed class CliError : Exception
{
    public string Code { get; }
    public int ExitCode { get; }
    public string? Hint { get; }
    public object? Details { get; }

    public CliError(string code, int exitCode, string message, string? hint = null, object? details = null) : base(message)
    {
        Code = code;
        ExitCode = exitCode;
        Hint = hint;
        Details = details;
    }

    public static CliError Usage(string message, string? hint = null) => new("usage", Exit.Usage, message, hint);
    public static CliError NotFound(string message, string? hint = null) => new("not_found", Exit.NotFound, message, hint);
    public static CliError Invalid(string message, string? hint = null, object? details = null) => new("invalid", Exit.Invalid, message, hint, details);
    public static CliError SignIn(string message, string site) =>
        new("sign_in_required", Exit.SignInRequired, message,
            $"Ask the user to run: cpm connection login --site {site}  (it opens their browser; you cannot complete it for them)");
}

/// <summary>A command's result plus the exit code it implies (a finished job with failures is still a successful command).</summary>
public sealed record Outcome(object? Data, int ExitCode = Exit.Ok);

/// <summary>An option. <see cref="ValueName"/> null makes it a flag.</summary>
public sealed record Opt(string Name, string? ValueName, string Help, bool Required = false);

public sealed class Command
{
    public required string[] Path { get; init; }
    public required string Summary { get; init; }
    /// <summary>Positional arguments, for example "&lt;file&gt;".</summary>
    public string[] Args { get; init; } = Array.Empty<string>();
    public Opt[] Options { get; init; } = Array.Empty<Opt>();
    public string[] Examples { get; init; } = Array.Empty<string>();
    /// <summary>Extra lines for the help of this command only.</summary>
    public string? Notes { get; init; }
    /// <summary>Opens a browser and waits for a person. Agents must hand these to the user.</summary>
    public bool Interactive { get; init; }
    /// <summary>Internal plumbing, left out of help and the guide.</summary>
    public bool Hidden { get; init; }
    public required Func<Invocation, Task<Outcome>> Run { get; init; }

    public string Name => string.Join(' ', Path);
    public string Usage => "cpm " + Name + (Args.Length > 0 ? " " + string.Join(' ', Args) : "") +
                           string.Concat(Options.Where(o => o.Required).Select(o => $" --{o.Name} {o.ValueName}"));
}

public sealed class Invocation
{
    public List<string> Positionals { get; } = new();
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string name) => Values.ContainsKey(name);
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public string Arg(int index, string what) =>
        index < Positionals.Count ? Positionals[index] : throw CliError.Usage($"Missing {what}.");

    public int Int(string name, int fallback)
    {
        if (Get(name) is not { } text) return fallback;
        return int.TryParse(text, out var value) ? value : throw CliError.Usage($"--{name} needs a number, got '{text}'.");
    }

    public long Long(int index, string what)
    {
        var text = Arg(index, what);
        return long.TryParse(text, out var value) ? value : throw CliError.Usage($"{what} has to be a number, got '{text}'.");
    }
}

/// <summary>
/// Parses the command line against the command table, runs the command and prints one JSON document.
///
/// Conventions, the same for every command:
///   - stdout carries exactly one JSON object: { "ok": true, "data": ... } or
///     { "ok": false, "error": { "code", "message", "hint" } };
///   - anything meant for a person watching (progress of an interactive sign-in) goes to stderr;
///   - nothing ever prompts on stdin.
/// </summary>
public static class Runner
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool _pretty;

    public static async Task<int> RunAsync(string[] args, IReadOnlyList<Command> commands)
    {
        // UTF-8 whatever the console's code page is, so names in any language survive a pipe.
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { /* no console attached */ }
        var tokens = args.ToList();
        try
        {
            // Global options, wherever they appear.
            _pretty = Take(tokens, "--pretty");
            if (tokens.Count > 0 && tokens[^1].Equals("--home", StringComparison.OrdinalIgnoreCase))
                throw CliError.Usage("--home needs a folder.", "Example: cpm connection list --home C:\\cpm-test");
            if (TakeValue(tokens, "--home") is { } home) Environment.SetEnvironmentVariable("CPM_HOME", Path.GetFullPath(home));
            var help = Take(tokens, "--help") | Take(tokens, "-h");
            if (tokens.Count > 0 && tokens[0] == "help") { help = true; tokens.RemoveAt(0); }

            if (tokens.Count == 0)
            {
                Console.Out.Write(Help.Overview(commands));
                return help ? Exit.Ok : Exit.Usage;
            }

            var command = Match(tokens, commands, out var consumed);
            if (command is null)
            {
                var group = commands.Where(c => !c.Hidden && c.Path[0].Equals(tokens[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (help && group.Count > 0)
                {
                    Console.Out.Write(Help.Group(tokens[0], group));
                    return Exit.Ok;
                }
                throw CliError.Usage($"Unknown command: cpm {string.Join(' ', tokens.Take(2))}",
                    group.Count > 0
                        ? "Commands here: " + string.Join(", ", group.Select(c => c.Name))
                        : "Run: cpm --help");
            }
            if (help)
            {
                Console.Out.Write(Help.For(command));
                return Exit.Ok;
            }

            var invocation = Parse(command, tokens.Skip(consumed).ToList());
            var outcome = await command.Run(invocation);
            Write(new { ok = true, data = outcome.Data });
            return outcome.ExitCode;
        }
        catch (CliError ex)
        {
            Write(new { ok = false, error = new { code = ex.Code, message = ex.Message, hint = ex.Hint, details = ex.Details } });
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            var error = Translate(ex);
            Write(new { ok = false, error = new { code = error.Code, message = error.Message, hint = error.Hint, details = error.Details } });
            return error.ExitCode;
        }
    }

    /// <summary>
    /// Turns what the engine and the network throw into an error with a stable code and a next step.
    /// The raw text of an HTTP failure (a long request URL, a server stack) is not something a caller
    /// can act on, so it is replaced, never passed through.
    /// </summary>
    public static CliError Translate(Exception ex)
    {
        switch (ex)
        {
            case CliError known:
                return known;
            case Core.Auth.SignInRequiredException:
                return new CliError("sign_in_required", Exit.SignInRequired, ex.Message, "Ask the user to run: cpm connection login --site <url>");
            case Core.Http.SpRestException rest:
                var address = rest.Url.Split("/_api/")[0];
                return rest.StatusCode switch
                {
                    404 => CliError.NotFound($"SharePoint has nothing at {address} (HTTP 404).",
                        "Check the site URL. See the subsites of a site with: cpm site webs --site <parent url>"),
                    401 or 403 => new CliError("access_denied", Exit.Failed,
                        $"SharePoint refused access to {address} (HTTP {rest.StatusCode}).",
                        "The signed-in account, or the app, has no rights on that site. Check with: cpm connection test --site <url>"),
                    _ => new CliError("sharepoint_error", Exit.Failed, $"SharePoint answered HTTP {rest.StatusCode} for {address}.", null),
                };
            case Microsoft.SharePoint.Client.ServerException server:
                return new CliError("sharepoint_error", Exit.Failed, server.Message, null);
            case System.Security.Cryptography.CryptographicException:
                return CliError.Invalid("The certificate could not be opened: the password is wrong, or the file is not a certificate with a private key.",
                    "Save the connection again with the right password: cpm connection add --site <url> --sign-in certificate --pfx <file> --pfx-password-env <variable>");
            case HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException:
                return new CliError("network", Exit.Failed, "Could not reach the server: " + ex.Message.Split('\n')[0], "Check the address and the network, then try again.");
            default:
                return new CliError("failed", Exit.Failed, ex.Message.Split('\n')[0], null);
        }
    }

    private static Command? Match(List<string> tokens, IReadOnlyList<Command> commands, out int consumed)
    {
        // Longest path first, so "job start" wins over a hypothetical "job".
        foreach (var command in commands.OrderByDescending(c => c.Path.Length))
        {
            if (tokens.Count < command.Path.Length) continue;
            if (command.Path.Select((part, i) => part.Equals(tokens[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            {
                consumed = command.Path.Length;
                return command;
            }
        }
        consumed = 0;
        return null;
    }

    private static Invocation Parse(Command command, List<string> tokens)
    {
        var invocation = new Invocation();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--"))
            {
                invocation.Positionals.Add(token);
                continue;
            }
            var name = token[2..];
            string? inline = null;
            var equals = name.IndexOf('=');
            if (equals >= 0) { inline = name[(equals + 1)..]; name = name[..equals]; }
            var option = command.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw CliError.Usage($"Unknown option --{name} for: cpm {command.Name}",
                    command.Options.Length > 0 ? "Options: " + string.Join(", ", command.Options.Select(o => "--" + o.Name)) : "This command takes no options.");
            if (option.ValueName is null)
            {
                invocation.Values[option.Name] = "true";
                continue;
            }
            if (inline is null)
            {
                if (i + 1 >= tokens.Count) throw CliError.Usage($"--{option.Name} needs a value ({option.ValueName}).");
                inline = tokens[++i];
            }
            invocation.Values[option.Name] = inline;
        }
        if (invocation.Positionals.Count > command.Args.Length)
            throw CliError.Usage($"Too many arguments for: cpm {command.Name}", "Usage: " + command.Usage);
        if (invocation.Positionals.Count < command.Args.Count(a => a.StartsWith('<')))
            throw CliError.Usage($"Missing {command.Args[invocation.Positionals.Count]}.", "Usage: " + command.Usage);
        foreach (var required in command.Options.Where(o => o.Required && !invocation.Has(o.Name)))
            throw CliError.Usage($"Missing --{required.Name} {required.ValueName}.", "Usage: " + command.Usage);
        return invocation;
    }

    private static bool Take(List<string> tokens, string flag)
    {
        var index = tokens.FindIndex(t => t.Equals(flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        tokens.RemoveAt(index);
        return true;
    }

    private static string? TakeValue(List<string> tokens, string name)
    {
        var index = tokens.FindIndex(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= tokens.Count) return null;
        var value = tokens[index + 1];
        tokens.RemoveRange(index, 2);
        return value;
    }

    private static void Write(object document)
    {
        var options = _pretty ? new JsonSerializerOptions(Json) { WriteIndented = true } : Json;
        Console.Out.WriteLine(JsonSerializer.Serialize(document, options));
    }

    /// <summary>For a person watching an interactive command. Never part of the result.</summary>
    public static void Note(string line) => Console.Error.WriteLine(line);
}

/// <summary>Help text, generated from the command table so it cannot drift from what the tool accepts.</summary>
public static class Help
{
    public static string Overview(IReadOnlyList<Command> commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("cpm: CleverPoint Migrator command line. Copies SharePoint Online lists, libraries, files,");
        sb.AppendLine("pages and permissions between sites and tenants.");
        sb.AppendLine();
        sb.AppendLine("AI agents: run `cpm agent guide` first. It explains the workflow and every command.");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        var width = commands.Where(c => !c.Hidden).Max(c => c.Name.Length) + 2;
        foreach (var command in commands.Where(c => !c.Hidden))
            sb.AppendLine("  " + command.Name.PadRight(width) + command.Summary + (command.Interactive ? " (opens a browser)" : ""));
        sb.AppendLine();
        sb.AppendLine("Every command prints one JSON object. Add --pretty to indent it, --help for a command's options.");
        sb.AppendLine("--home <folder> uses another settings folder (connections, history).");
        return sb.ToString();
    }

    public static string Group(string name, List<Command> commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"cpm {name} commands:");
        var width = commands.Max(c => c.Name.Length) + 2;
        foreach (var command in commands) sb.AppendLine("  " + command.Name.PadRight(width) + command.Summary);
        return sb.ToString();
    }

    public static string For(Command command)
    {
        var sb = new StringBuilder();
        sb.AppendLine(command.Summary);
        sb.AppendLine();
        sb.AppendLine("Usage: " + command.Usage + (command.Options.Any(o => !o.Required) ? " [options]" : ""));
        if (command.Options.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Options:");
            var width = command.Options.Max(o => OptionLabel(o).Length) + 2;
            foreach (var option in command.Options)
                sb.AppendLine("  " + OptionLabel(option).PadRight(width) + option.Help + (option.Required ? " (required)" : ""));
        }
        if (command.Notes is { } notes)
        {
            sb.AppendLine();
            sb.AppendLine(notes);
        }
        if (command.Interactive)
        {
            sb.AppendLine();
            sb.AppendLine("This command opens a browser and waits for a person to finish signing in.");
        }
        if (command.Examples.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Examples:");
            foreach (var example in command.Examples) sb.AppendLine("  " + example);
        }
        return sb.ToString();
    }

    public static string OptionLabel(Opt option) => "--" + option.Name + (option.ValueName is null ? "" : " " + option.ValueName);
}
