using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>One place a skill file lives, and what state it is in.</summary>
public sealed record SkillLocation(string Target, string Agent, string Path, string State, int InstalledVersion);

/// <summary>
/// Puts a small skill file where AI coding agents look for skills, so an agent knows this tool exists
/// and how to start using it without the user having to explain.
///
/// The skill is a pointer, on purpose. It says when to use the tool and to run "cpm agent guide"; the
/// guide itself comes from the installed program, so it always describes the installed version. The
/// skill file therefore rarely changes, and when it does:
///   - the guide tells an agent holding an older skill to run "cpm agent install";
///   - a refresh after an app update rewrites the copies this tool installed, but only those nobody
///     has edited (their content still matches the hash recorded at install time).
/// A refresh never installs into a new place.
/// </summary>
public static class AgentSkill
{
    /// <summary>Bump only when <see cref="Content"/> changes. Independent of the app version.</summary>
    public const int Version = 4;

    public const string Name = "cleverpoint-migrator";

    /// <summary>The skill as written on this machine, outside WSL.</summary>
    public static string Content => ContentFor("local");

    /// <summary>
    /// Where the cpm program is, as the agent in that place has to write it: a Windows path, or from
    /// inside WSL the same file under /mnt. Null when cpm is not next to the running program.
    /// </summary>
    public static string? CpmPath(string target)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "cpm.exe" : "cpm");
        if (!File.Exists(path)) return null;
        if (!target.StartsWith("wsl:", StringComparison.Ordinal)) return path;
        // C:\Folder\cpm.exe is /mnt/c/Folder/cpm.exe inside WSL.
        return path.Length > 2 && path[1] == ':' ? $"/mnt/{char.ToLowerInvariant(path[0])}{path[2..].Replace('\\', '/')}" : path;
    }

    /// <summary>
    /// The skill for one place. It names where cpm is on this machine: an agent whose shell does not
    /// find "cpm" (the installer was not run, or the terminal is older than the install) would
    /// otherwise go looking for it all over the disk, or conclude that it is not installed.
    /// </summary>
    public static string ContentFor(string target) => Body + (CpmPath(target) is { } cpm
        ? $"\n\n## If the shell does not find cpm\n\nDo not search. Run it by its full path, in quotes:\n\n```\n{cpm}\n```\n\nIf that file is gone, the app was moved or removed: ask the user.\n"
        : "\n");

    private static string Body => $"""
        ---
        name: {Name}
        description: Use when copying or migrating SharePoint Online lists, libraries, folders, files, site pages or permissions between sites or tenants, when checking on such a migration, or when asked what is in a tenant or a set of sites (sites, OneDrives, lists, subsites, guests, customized forms, custom columns). Drives the CleverPoint Migrator command line (cpm), which does the copying and the reporting itself, over hundreds of sites at once. Do not script this by hand with PnP or REST when this tool is installed.
        ---

        # CleverPoint Migrator (cpm)

        skill-version: {Version}

        `cpm` copies SharePoint Online content between sites and tenants, records every item it handled,
        and runs reports over any number of sites. You hand it a job or a report and poll for progress.

        ## Start here, every time

        ```
        cpm agent guide --skill-version {Version}
        ```

        That prints the current workflow, every command, the job file fields and the exit codes for the
        installed version. Trust it over anything you remember: commands are added with app updates.
        If its first line says this skill is outdated, run `cpm agent install` once and carry on.

        ## Rules that do not change

        - Output is JSON on stdout: `ok`, then `data` or `error` (with `code`, `message` and a `hint` naming the next command). Read the hint.
        - Never ask the user for passwords or secrets, and never print them. Sign-in happens in the user's browser through `cpm connection login`, which the user has to complete.
        - Validate before running: `cpm job validate <file> --online`.
        - Jobs run detached. Start with `cpm job start`, then poll `cpm job status` or block with `cpm job wait`. Do not wait on the start command.
        - Look at failures with `cpm run items <runId> --status failed,warning` before reporting a job as done.
        - A job only changes the target site. Ask the user before a job that overwrites existing content or replaces permissions.
        - Reports: `cpm report list`, then `cpm report start`, `status`, and `rows` a page at a time. Never load a whole report; filter with `--where` and `--columns`, or hand the user a file with `cpm report export`.
        - Some reports are actions that change sites. Started plainly they only preview. Show the user what would change, and run `cpm report apply <previewRunId>` only after they say yes.

        ## On WSL

        The tool is a Windows program. Call it as `cpm.exe`, and pass file paths in Windows form
        (`wslpath -w ./job.json`).
        """.Replace("\r\n", "\n").TrimEnd('\n');

    private static readonly (string Agent, string Home, string SkillsDir)[] Agents =
    {
        ("claude", ".claude", ".claude/skills"),
        ("codex", ".codex", ".codex/skills"),
        ("gemini", ".gemini", ".gemini/skills"),
        // A shared location several agents read. Only used when the folder already exists.
        ("agents", ".agents", ".agents/skills"),
    };

    private sealed class ManifestEntry
    {
        public string Target { get; set; } = "";     // "local" or "wsl:<distro>"
        public string Agent { get; set; } = "";
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Version { get; set; }
        /// <summary>Where the skill said cpm is when it was written.</summary>
        public string? Cpm { get; set; }
        public DateTime InstalledUtc { get; set; }
    }

    private static string ManifestPath => System.IO.Path.Combine(UxSettings.Folder, "agent-skills.json");

    /// <summary>
    /// Installs the skill for every agent whose home folder exists, on this machine and (from Windows)
    /// in each WSL distribution. Nothing is assumed: a machine without WSL, or without any agent,
    /// simply yields an empty list.
    /// </summary>
    public static List<SkillLocation> Install(IReadOnlyCollection<string>? onlyAgents = null, bool includeWsl = true)
    {
        var manifest = LoadManifest();
        var results = new List<SkillLocation>();
        foreach (var (target, agent, path) in Discover(includeWsl))
        {
            if (onlyAgents is { Count: > 0 } && !onlyAgents.Contains(agent, StringComparer.OrdinalIgnoreCase)) continue;
            var ok = WriteFile(target, path, ContentFor(target));
            if (ok) Remember(manifest, target, agent, path);
            results.Add(new SkillLocation(target, agent, path, ok ? "installed" : "failed", ok ? Version : 0));
        }
        SaveManifest(manifest);
        return results;
    }

    /// <summary>Installs (or rewrites) the skill in one place found by <see cref="Discover"/>.</summary>
    public static bool InstallAt(string target, string agent, string path)
    {
        if (!WriteFile(target, path, ContentFor(target))) return false;
        var manifest = LoadManifest();
        Remember(manifest, target, agent, path);
        SaveManifest(manifest);
        return true;
    }

    /// <summary>Removes the skill from one place, with its folder when nothing else is in it.</summary>
    public static bool RemoveAt(string target, string path)
    {
        bool ok;
        try
        {
            if (target == "local")
            {
                if (File.Exists(path)) File.Delete(path);
                var folder = System.IO.Path.GetDirectoryName(path)!;
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                ok = true;
            }
            else
            {
                (ok, _) = Wsl(target["wsl:".Length..], $"rm -f {ShellPath(path)}; rmdir \"$(dirname {ShellPath(path)})\" 2>/dev/null; [ ! -e {ShellPath(path)} ]");
            }
        }
        catch { ok = false; }
        if (!ok) return false;
        var manifest = LoadManifest();
        manifest.RemoveAll(e => e.Target == target && e.Path == path);
        SaveManifest(manifest);
        return true;
    }

    /// <summary>
    /// Brings the copies this tool installed up to date. A copy is rewritten only when it is exactly what
    /// was installed (nobody edited it) and is older than the current skill. Called after an app update.
    /// </summary>
    public static List<SkillLocation> Refresh()
    {
        var manifest = LoadManifest();
        var results = new List<SkillLocation>();
        foreach (var entry in manifest.ToList())
        {
            // A copy installed at the current version needs no look. This keeps the check at app start
            // free: no file is read and no WSL distribution is woken up unless there is work to do.
            // The skill names where cpm is, so a copy written before the app was moved is as stale as an old one.
            var moved = !string.Equals(entry.Cpm ?? "", CpmPath(entry.Target) ?? "", StringComparison.OrdinalIgnoreCase);
            if (entry.Version >= Version && !moved)
            {
                results.Add(new SkillLocation(entry.Target, entry.Agent, entry.Path, "current", entry.Version));
                continue;
            }
            var state = StateOf(entry);
            if (state == "outdated" || (state == "current" && moved))
            {
                if (WriteFile(entry.Target, entry.Path, ContentFor(entry.Target)))
                {
                    Remember(manifest, entry.Target, entry.Agent, entry.Path);
                    state = "updated";
                }
                else state = "failed";
            }
            results.Add(new SkillLocation(entry.Target, entry.Agent, entry.Path, state, entry.Version));
        }
        SaveManifest(manifest);
        return results;
    }

    /// <summary>Where the skill is installed and whether each copy is current, outdated, edited or missing.</summary>
    public static List<SkillLocation> Status() =>
        LoadManifest().Select(e => new SkillLocation(e.Target, e.Agent, e.Path, StateOf(e), e.Version)).ToList();

    /// <summary>Agent homes found, whether or not the skill is installed there. For the "Use with AI agents" card.</summary>
    public static List<(string Target, string Agent, string Path)> Discover(bool includeWsl = true)
    {
        var found = new List<(string, string, string)>();
        // CPM_AGENT_HOME stands in for the user's home folder, so tests never touch real agent folders.
        var home = Environment.GetEnvironmentVariable("CPM_AGENT_HOME") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var (agent, agentHome, skillsDir) in Agents)
            if (Directory.Exists(System.IO.Path.Combine(home, agentHome)))
                found.Add(("local", agent, System.IO.Path.Combine(home, skillsDir.Replace('/', System.IO.Path.DirectorySeparatorChar), Name, "SKILL.md")));

        if (includeWsl && OperatingSystem.IsWindows())
            foreach (var distro in WslDistros())
            {
                var probe = string.Join("; ", Agents.Select(a => $"[ -d \"$HOME/{a.Home}\" ] && echo {a.Agent}"));
                var (ok, output) = Wsl(distro, probe + "; true");
                if (!ok) continue;
                foreach (var agent in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var match = Agents.FirstOrDefault(a => a.Agent == agent);
                    if (match.Agent is null) continue;
                    found.Add(($"wsl:{distro}", agent, $"~/{match.SkillsDir}/{Name}/SKILL.md"));
                }
            }
        return found;
    }

    private static string StateOf(ManifestEntry entry)
    {
        var current = ReadFile(entry.Target, entry.Path);
        if (current is null) return "missing";
        if (Hash(current) != entry.Hash) return "edited";
        return entry.Version < Version ? "outdated" : "current";
    }

    private static void Remember(List<ManifestEntry> manifest, string target, string agent, string path)
    {
        manifest.RemoveAll(e => e.Target == target && e.Path == path);
        manifest.Add(new ManifestEntry
        {
            Target = target, Agent = agent, Path = path, Hash = Hash(ContentFor(target)), Version = Version, Cpm = CpmPath(target), InstalledUtc = DateTime.UtcNow,
        });
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));

    private static List<ManifestEntry> LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
                return JsonSerializer.Deserialize<List<ManifestEntry>>(File.ReadAllText(ManifestPath)) ?? new();
        }
        catch { /* an unreadable manifest only means nothing is known to be installed */ }
        return new();
    }

    private static void SaveManifest(List<ManifestEntry> manifest)
    {
        try
        {
            Directory.CreateDirectory(UxSettings.Folder);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    // ---- files, locally or inside a WSL distribution ----------------------------------------

    private static bool WriteFile(string target, string path, string content)
    {
        try
        {
            if (target == "local")
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return true;
            }
            var distro = target["wsl:".Length..];
            // The text travels as base64 inside the script, so the file gets exactly these bytes.
            var encoded = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(content.Replace("\r\n", "\n")));
            var (ok, _) = Wsl(distro, $"mkdir -p \"$(dirname {ShellPath(path)})\" && printf %s '{encoded}' | base64 -d > {ShellPath(path)}");
            return ok;
        }
        catch { return false; }
    }

    private static string? ReadFile(string target, string path)
    {
        try
        {
            if (target == "local") return File.Exists(path) ? File.ReadAllText(path) : null;
            var (ok, output) = Wsl(target["wsl:".Length..], $"cat {ShellPath(path)}");
            return ok ? output : null;
        }
        catch { return null; }
    }

    /// <summary>"~/x/y" as a shell word that still expands the home folder.</summary>
    private static string ShellPath(string path) =>
        path.StartsWith("~/") ? "\"$HOME/" + path[2..].Replace("\"", "\\\"") + "\"" : "\"" + path.Replace("\"", "\\\"") + "\"";

    private static List<string> WslDistros()
    {
        // "wsl.exe -l -q" prints UTF-16. A machine without WSL either has no wsl.exe or prints nothing useful.
        var (ok, output) = Run("wsl.exe", new[] { "-l", "-q" }, null, Encoding.Unicode);
        if (!ok) return new();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('\0', '\r', ' '))
            .Where(d => d.Length > 0 && !d.StartsWith("docker-", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // The script goes in on standard input. As a command-line argument it is re-read by the distro's
    // login shell, which mangles quotes and "$(...)", and the write failed on real machines.
    private static (bool Ok, string Output) Wsl(string distro, string script) =>
        Run("wsl.exe", new[] { "-d", distro, "--exec", "sh", "-s" }, script + "\n", new UTF8Encoding(false));

    private static (bool Ok, string Output) Run(string file, string[] args, string? stdin, Encoding outputEncoding)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = outputEncoding,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return (false, "");
            if (stdin != null)
            {
                var bytes = new UTF8Encoding(false).GetBytes(stdin.Replace("\r\n", "\n"));
                process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            }
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            // A distribution that has to start up can take a few seconds; a broken one must not hang the app.
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch { /* already gone */ }
                return (false, "");
            }
            return (process.ExitCode == 0, output.GetAwaiter().GetResult());
        }
        catch
        {
            return (false, "");   // no wsl.exe on this machine
        }
    }
}
