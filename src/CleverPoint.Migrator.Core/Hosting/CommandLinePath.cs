namespace CleverPoint.Migrator.Core.Hosting;

/// <summary>
/// Whether the command line (cpm) can be found by name, and adding its folder to the user's PATH when
/// it cannot. The installer does this by itself; the portable zip does not, so the desktop app offers
/// it. Only the current user's PATH is touched, never the machine's.
/// </summary>
public static class CommandLinePath
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "cpm.exe" : "cpm";

    /// <summary>The folder this application runs from, which is where cpm ships.</summary>
    public static string Folder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The command line ships next to this application.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(Folder, ExecutableName));

    /// <summary>The user's PATH already contains the folder cpm is in.</summary>
    public static bool IsOnPath
    {
        get
        {
            var target = OperatingSystem.IsWindows() ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Process;
            var paths = (Environment.GetEnvironmentVariable("PATH", target) ?? "") + Path.PathSeparator +
                        (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "");
            return paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(Environment.ExpandEnvironmentVariables(p).TrimEnd('\\', '/'), Folder,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Appends the folder to the current user's PATH (Windows). New terminals pick it up; ones that are
    /// already open do not. Returns false where there is no per-user PATH to edit.
    /// </summary>
    public static bool AddToUserPath()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (IsOnPath) return true;
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var updated = current.Length == 0 ? Folder : current.TrimEnd(';') + ";" + Folder;
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        return true;
    }
}
