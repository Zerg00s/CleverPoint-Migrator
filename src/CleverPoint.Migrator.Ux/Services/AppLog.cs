using System.Text;

namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Minimal crash/error logger. Appends unhandled exceptions to a rolling text
/// file under %AppData%\CleverPoint Migrator\logs so the user can read what went
/// wrong from Settings > Troubleshoot > Open Logs. Logging must never throw, so
/// every path swallows its own failures.
/// </summary>
public static class AppLog
{
    public static string Folder => Path.Combine(UxSettings.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "app.log");

    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void EnsureFolder()
    {
        try { Directory.CreateDirectory(Folder); } catch { /* best effort */ }
    }

    public static void Error(string context, object? detail)
    {
        try
        {
            EnsureFolder();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR {context}"
                       + (detail != null ? Environment.NewLine + detail : "")
                       + Environment.NewLine + Environment.NewLine;
            lock (Gate)
            {
                RollIfLarge();
                // FileShare.ReadWrite so a second process (or the sign-in helper) writing the
                // same log does not make this append throw and lose the record.
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs, Encoding.UTF8);
                sw.Write(line);
            }
        }
        catch { /* logging must never throw */ }
    }

    // Keep the log from growing without bound: once it passes MaxBytes, move it
    // aside to app.previous.log (overwriting any older backup) and start fresh.
    private static void RollIfLarge()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            var bak = Path.Combine(Folder, "app.previous.log");
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(FilePath, bak);
        }
        catch { /* best effort */ }
    }
}
